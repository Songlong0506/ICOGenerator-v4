using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Models;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Logging;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Templates;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Agents;

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
                Content = _promptTemplateService.Get("BA/requirement-draft.v1.md")
            },
            new()
            {
                Role = "user",
                Content = prompt
            }
        };

        var callResult = await _llm.ChatWithLogAsync(model, messages, ba.Temperature);
        await _modelCallLogger.LogAsync(projectId, ba, callResult, 1, "BARequirementDraft");

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
