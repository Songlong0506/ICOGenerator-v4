using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Models;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Templates;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Agents;

public class BARequirementService
{
    private readonly AppDbContext _db;
    private readonly LocalLlmClient _llm;
    private readonly RequirementTemplateService _templateService;
    private readonly RequirementPromptBuilder _promptBuilder;
    private readonly RequirementResponseParser _responseParser;
    private readonly RequirementDocumentGenerator _documentGenerator;

    public BARequirementService(
        AppDbContext db,
        LocalLlmClient llm,
        RequirementTemplateService templateService,
        RequirementPromptBuilder promptBuilder,
        RequirementResponseParser responseParser,
        RequirementDocumentGenerator documentGenerator)
    {
        _db = db;
        _llm = llm;
        _templateService = templateService;
        _promptBuilder = promptBuilder;
        _responseParser = responseParser;
        _documentGenerator = documentGenerator;
    }

    public async Task GenerateOrUpdateDraftAsync(Guid projectId, string userMessage)
    {
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

        var prompt = _promptBuilder.Build(
            project,
            userMessage,
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
                Content = """
Bạn là BA Agent của công ty.

Nhiệm vụ duy nhất:
1. Trao đổi với user để làm rõ requirement.
2. Viết/cập nhật dữ liệu cho 5 tài liệu:
   - BRD.docx
   - SRS.docx
   - FSD.docx
   - UserStories.docx
   - AIDesignSpec.docx
3. BRD, SRS và FSD phải bám theo template chuẩn công ty.
4. AIDesignSpec là tài liệu tối ưu cho AI Developer Agent generate mockup/POC/code.
5. Không được viết source code, build/run/test code, hoặc đóng vai Developer.
6. Nếu thiếu thông tin quan trọng, hãy ghi vào openQuestions và assistantMessage.

Luôn trả về JSON duy nhất theo format:
{
  "assistantMessage": "...",
  "brd": {
    "projectName": "...",
    "executiveSummary": "...",
    "businessContext": "...",
    "problemStatement": "...",
    "businessObjectives": "...",
    "inScope": "...",
    "outOfScope": "...",
    "stakeholders": "...",
    "businessRequirements": "...",
    "asIsProcess": "...",
    "toBeProcess": "...",
    "risks": "...",
    "openQuestions": "..."
  },
  "srs": {
    "projectName": "...",
    "purpose": "...",
    "scope": "...",
    "userGroups": "...",
    "assumptions": "...",
    "constraints": "...",
    "functionalRequirements": "...",
    "nonFunctionalRequirements": "...",
    "uiRequirements": "...",
    "apiRequirements": "...",
    "dataRequirements": "...",
    "deploymentRequirements": "...",
    "testingRequirements": "...",
    "openIssues": "..."
  },
  "fsd": {
    "projectName": "...",
    "moduleScope": "...",
    "purpose": "...",
    "scope": "...",
    "functionalArchitecture": "...",
    "actors": "...",
    "navigationStructure": "...",
    "screenList": "...",
    "uiSpecification": "...",
    "featureDetails": "...",
    "businessRules": "...",
    "mainFlows": "...",
    "alternativeFlows": "...",
    "apiReferences": "...",
    "dataReferences": "...",
    "openQuestions": "..."
  },
  "userStories": { "content": "..." },
  "aiDesignSpec": { "content": "..." }
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

        var result = _responseParser.Parse(callResult.Content, project, userMessage);
        await _documentGenerator.GenerateDraftDocxFiles(project, ba.Id, result);

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "assistant",
            Message = result.AssistantMessage,
            TokenUsed = EstimateTokens(result.AssistantMessage)
        });

        await _db.SaveChangesAsync();
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

    private static int EstimateTokens(string? text)
        => string.IsNullOrWhiteSpace(text) ? 0 : Math.Max(1, text.Length / 4);
}
