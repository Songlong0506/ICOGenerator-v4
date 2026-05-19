using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Agents;

public class BARequirementService
{
    private readonly AppDbContext _db;
    private readonly LocalLlmClient _llm;
    private readonly IConfiguration _configuration;

    public BARequirementService(
        AppDbContext db,
        LocalLlmClient llm,
        IConfiguration configuration)
    {
        _db = db;
        _llm = llm;
        _configuration = configuration;
    }

    public async Task GenerateOrUpdateDraftAsync(Guid projectId, string userMessage)
    {
        var project = await _db.Projects
            .Include(x => x.Documents)
            .Include(x => x.Conversations)
            .FirstAsync(x => x.Id == projectId);

        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstAsync(x => x.Name == "BA");

        var model = ba.AiModel ?? await _db.AiModels.FirstAsync(x => x.IsDefault);

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "user",
            Message = userMessage,
            TokenUsed = EstimateTokens(userMessage)
        });

        await _db.SaveChangesAsync();

        var currentBrd = GetDoc(project, "BRD.md");
        var currentSrs = GetDoc(project, "SRS.md");
        var currentStories = GetDoc(project, "UserStories.md");

        var prompt = BuildBAPrompt(project, userMessage, currentBrd, currentSrs, currentStories);

        var messages = new List<ChatMessageDto>
        {
            new()
            {
                Role = "system",
                Content = """
Bạn là BA Agent.
Bạn chỉ được làm các việc:
1. Trao đổi với user để làm rõ requirement.
2. Viết/cập nhật 3 tài liệu: BRD, SRS, UserStories.
3. Không được viết source code.
4. Không được gọi tool để build/run/code.
5. Không được đề xuất implementation chi tiết như Developer.

Luôn trả về JSON duy nhất theo format:
{
  "assistantMessage": "...",
  "brd": "...markdown...",
  "srs": "...markdown...",
  "userStories": "...markdown..."
}
"""
            },
            new()
            {
                Role = "user",
                Content = prompt
            }
        };

        var callResult = await _llm.ChatWithLogAsync(model, messages, ba.Temperature);
        await SaveModelCallLog(projectId, ba, callResult, "BARequirementDraft");
        var response = callResult.Content;

        var result = ParseBAResponse(response, userMessage);

        await UpsertDraftDocument(projectId, ba.Id, "BRD.md", result.Brd);
        await UpsertDraftDocument(projectId, ba.Id, "SRS.md", result.Srs);
        await UpsertDraftDocument(projectId, ba.Id, "UserStories.md", result.UserStories);

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "assistant",
            Message = result.AssistantMessage,
            TokenUsed = EstimateTokens(result.AssistantMessage)
        });

        await _db.SaveChangesAsync();

        WriteDraftFiles(project.Name, result);
    }

    private async Task SaveModelCallLog(Guid projectId, Agent agent, LocalLlmCallResult callResult, string purpose)
    {
        _db.AgentModelCallLogs.Add(new AgentModelCallLog
        {
            ProjectId = projectId,
            AgentId = agent.Id,
            AgentName = agent.Name,
            ModelName = callResult.ModelName,
            ModelId = callResult.ModelId,
            Endpoint = callResult.Endpoint,
            RequestJson = callResult.RequestJson,
            ResponseText = callResult.ResponseText,
            ExtractedContent = callResult.ExtractedContent,
            ErrorMessage = callResult.ErrorMessage,
            PromptTokens = callResult.PromptTokens,
            CompletionTokens = callResult.CompletionTokens,
            TotalTokens = callResult.TotalTokens,
            DurationMs = callResult.DurationMs,
            HttpStatusCode = callResult.HttpStatusCode,
            IsSuccess = callResult.IsSuccess,
            Step = 1,
            Purpose = purpose
        });

        await _db.SaveChangesAsync();
    }

    private static string GetDoc(Project project, string fileName)
    {
        return project.Documents
            .Where(x => x.VersionName == "draft" && x.FileName == fileName)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.Content)
            .FirstOrDefault() ?? "";
    }

    private static string BuildBAPrompt(
        Project project,
        string userMessage,
        string currentBrd,
        string currentSrs,
        string currentStories)
    {
        return $"""
Project: {project.Name}
Description: {project.Description}

User message:
{userMessage}

Current BRD:
{currentBrd}

Current SRS:
{currentSrs}

Current UserStories:
{currentStories}

Hãy cập nhật lại 3 tài liệu requirement dựa trên thông tin mới nhất.
Không viết code.
""";
    }

    private async Task UpsertDraftDocument(
        Guid projectId,
        Guid agentId,
        string fileName,
        string content)
    {
        var doc = await _db.ProjectDocuments
            .FirstOrDefaultAsync(x =>
                x.ProjectId == projectId &&
                x.VersionName == "draft" &&
                x.FileName == fileName);

        if (doc == null)
        {
            _db.ProjectDocuments.Add(new ProjectDocument
            {
                ProjectId = projectId,
                AgentId = agentId,
                Folder = "docs/draft",
                VersionName = "draft",
                IsApproved = false,
                FileName = fileName,
                Content = content,
                TokenUsed = EstimateTokens(content)
            });
        }
        else
        {
            doc.Content = content;
            doc.TokenUsed = EstimateTokens(content);
            doc.CreatedAt = DateTime.UtcNow;
        }
    }

    private void WriteDraftFiles(string projectName, BARequirementResult result)
    {
        var root = _configuration["AgentWorkspace:RootPath"]
            ?? throw new InvalidOperationException("AgentWorkspace:RootPath is missing.");

        var projectFolder = MakeSafeFolderName(projectName);
        var draftPath = Path.Combine(root, projectFolder, "docs", "draft");

        Directory.CreateDirectory(draftPath);

        File.WriteAllText(Path.Combine(draftPath, "BRD.md"), result.Brd);
        File.WriteAllText(Path.Combine(draftPath, "SRS.md"), result.Srs);
        File.WriteAllText(Path.Combine(draftPath, "UserStories.md"), result.UserStories);
    }

    private static BARequirementResult ParseBAResponse(string response, string userMessage)
    {
        try
        {
            var json = JsonExtractor.Extract(response);

            var result = JsonSerializer.Deserialize<BARequirementResult>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (result != null)
                return result;
        }
        catch
        {
        }

        return new BARequirementResult
        {
            AssistantMessage = "Tôi đã cập nhật requirement draft dựa trên thông tin bạn cung cấp.",
            Brd = $"# BRD\n\n## Tổng quan\n{userMessage}",
            Srs = $"# SRS\n\n## Functional Requirements\n{userMessage}",
            UserStories = $"# User Stories\n\n- Là người dùng, tôi muốn {userMessage}"
        };
    }

    private static int EstimateTokens(string text)
        => Math.Max(1, text.Length / 4);

    private static string MakeSafeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        return name.Replace(" ", "-").ToLowerInvariant();
    }
}

public class BARequirementResult
{
    public string AssistantMessage { get; set; } = "";
    public string Brd { get; set; } = "";
    public string Srs { get; set; } = "";
    public string UserStories { get; set; } = "";
}