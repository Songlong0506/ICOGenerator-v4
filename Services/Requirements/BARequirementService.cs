using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Logging;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements.Templates;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

public class BARequirementService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly RequirementTemplateService _templateService;
    private readonly RequirementPromptBuilder _promptBuilder;
    private readonly RequirementResponseParser _responseParser;
    private readonly RequirementDocumentGenerator _documentGenerator;
    private readonly IModelCallLogger _modelCallLogger;
    private readonly PromptTemplateService _promptTemplateService;

    public BARequirementService(
        AppDbContext db,
        ILlmClient llm,
        RequirementTemplateService templateService,
        RequirementPromptBuilder promptBuilder,
        RequirementResponseParser responseParser,
        RequirementDocumentGenerator documentGenerator,
        IModelCallLogger modelCallLogger,
        PromptTemplateService promptTemplateService)
    {
        _db = db;
        _llm = llm;
        _templateService = templateService;
        _promptBuilder = promptBuilder;
        _responseParser = responseParser;
        _documentGenerator = documentGenerator;
        _modelCallLogger = modelCallLogger;
        _promptTemplateService = promptTemplateService;
    }

    public async Task ChatAsync(Guid projectId, string userMessage)
    {
        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst);

        var model = ba.AiModel ?? throw new InvalidOperationException("BA agent model is not configured.");

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "user",
            Message = userMessage,
            TokenUsed = TokenEstimator.Estimate(userMessage)
        });
        await _db.SaveChangesAsync();

        // Lấy tối đa 20 lượt gần nhất để giữ ngữ cảnh mà vẫn nhẹ token.
        var recent = await _db.AgentConversations
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(20)
            .ToListAsync();
        recent.Reverse();

        var messages = new List<ChatMessageDto>
        {
            new()
            {
                Role = "system",
                Content = _promptTemplateService.Get("BA/requirement-chat.v1.md")
            }
        };
        messages.AddRange(recent.Select(c => new ChatMessageDto
        {
            Role = c.Role == "assistant" ? "assistant" : "user",
            Content = c.Message
        }));

        var callResult = await _llm.ChatWithLogAsync(model, messages, ba.Temperature);
        await _modelCallLogger.LogAsync(projectId, ba, callResult, 1, "BAChat");

        var reply = (callResult.Content ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(reply))
            reply = "Đã ghi nhận. Bạn có thể bổ sung thêm yêu cầu, hoặc bấm \"Write Requirement\" để tạo tài liệu.";

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "assistant",
            Message = reply,
            TokenUsed = TokenEstimator.Estimate(reply)
        });
        await _db.SaveChangesAsync();
    }

    /// <param name="onProgress">
    /// Callback (kind, message, detail) để báo tiến độ live cho UI. Có thể null khi gọi đồng bộ.
    /// </param>
    public async Task GenerateOrUpdateDraftAsync(Guid projectId, Action<string, string, string?>? onProgress = null)
    {
        void Report(string kind, string message, string? detail = null) => onProgress?.Invoke(kind, message, detail);

        Report("setup", "Đang đọc hội thoại và template tài liệu…");

        var brdTemplate = _templateService.GetBrdTemplate();
        var srsTemplate = _templateService.GetSrsTemplate();
        var fsdTemplate = _templateService.GetFsdTemplate();
        var userStoriesTemplate = _templateService.GetUserStoriesTemplate();

        var project = await _db.Projects
            .Include(x => x.Documents)
            .Include(x => x.Conversations)
            .FirstAsync(x => x.Id == projectId);

        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst);

        var model = ba.AiModel ?? throw new InvalidOperationException("BA agent model is not configured.");

        // Gộp toàn bộ yêu cầu user đã nói trong hội thoại thành brief đầu vào.
        var requirementBrief = BuildRequirementBrief(project.Conversations);

        Report("thinking", "Đang tổng hợp yêu cầu từ hội thoại…", requirementBrief);

        var prompt = _promptBuilder.Build(
            project,
            requirementBrief,
            GetDoc(project, "BRD.docx"),
            GetDoc(project, "SRS.docx"),
            GetDoc(project, "FSD.docx"),
            GetDoc(project, "UserStories.docx"),
            GetDoc(project, "AIDesignSpec.docx"),
            brdTemplate,
            srsTemplate,
            fsdTemplate,
            userStoriesTemplate);

        var messages = new List<ChatMessageDto>
        {
            new()
            {
                Role = "system",
                Content = _promptTemplateService.Get("BA/requirement-draft.v1.md")
            },
            new()
            {
                Role = "user",
                Content = prompt
            }
        };

        Report("tool", "Đang gọi AI để soạn BRD, SRS, FSD, User Stories, AI Design Spec…");

        var callResult = await _llm.ChatWithLogAsync(model, messages, ba.Temperature);
        await _modelCallLogger.LogAsync(projectId, ba, callResult, 1, "BARequirementDraft");

        Report("observation", "AI đã trả về nội dung, đang phân tích kết quả…");

        var result = _responseParser.Parse(callResult.Content, project, requirementBrief);

        Report("tool", "Đang tạo/cập nhật file tài liệu (.docx)…");

        await _documentGenerator.GenerateDraftDocxFiles(project, ba.Id, result);

        var assistantMessage = string.IsNullOrWhiteSpace(result.AssistantMessage)
            ? "Đã tạo/cập nhật 5 tài liệu: BRD, SRS, FSD, User Stories, AI Design Spec."
            : result.AssistantMessage;

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "assistant",
            Message = assistantMessage,
            TokenUsed = TokenEstimator.Estimate(assistantMessage)
        });

        await _db.SaveChangesAsync();

        Report("final", "Đã tạo/cập nhật tài liệu.", assistantMessage);
    }

    private static string BuildRequirementBrief(IEnumerable<AgentConversation> conversations)
    {
        var userTurns = conversations
            .OrderBy(c => c.CreatedAt)
            .Where(c => c.Role != "assistant")
            .Select(c => (c.Message ?? string.Empty).Trim())
            .Where(m => m.Length > 0)
            .ToList();

        return userTurns.Count == 0
            ? "(Chưa có yêu cầu nào được ghi nhận.)"
            : string.Join("\n", userTurns.Select(m => "- " + m));
    }

    private static string GetDoc(Project project, string fileName)
    {
        return project.Documents
            .Where(x => x.VersionName == "draft" && x.FileName == fileName)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.Content)
            .FirstOrDefault() ?? "";
    }
}
