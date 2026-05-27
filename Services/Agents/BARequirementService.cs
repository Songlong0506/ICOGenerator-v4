using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Models;
using ICOGenerator.Services.Templates;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ICOGenerator.Services.Agents;

public class BARequirementService
{
    private readonly AppDbContext _db;
    private readonly LocalLlmClient _llm;
    private readonly IConfiguration _configuration;
    private readonly RequirementTemplateService _templateService;
    private readonly IWebHostEnvironment _env;
    private readonly DocxTemplateWriter _docxWriter;

    public BARequirementService(
       AppDbContext db,
       LocalLlmClient llm,
       IConfiguration configuration,
       RequirementTemplateService templateService,
       IWebHostEnvironment env,
       DocxTemplateWriter docxWriter)
    {
        _db = db;
        _llm = llm;
        _configuration = configuration;
        _templateService = templateService;
        _env = env;
        _docxWriter = docxWriter;
    }

    public async Task GenerateOrUpdateDraftAsync(Guid projectId, string userMessage)
    {
        var brdTemplate = _templateService.GetBrdTemplate();
        var srsTemplate = _templateService.GetSrsTemplate();
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

        var currentBrd = GetDoc(project, "BRD.docx");
        var currentSrs = GetDoc(project, "SRS.docx");
        var currentStories = GetDoc(project, "UserStories.docx");

        var prompt = BuildBAPrompt(project, userMessage, currentBrd, currentSrs, currentStories, brdTemplate, srsTemplate, userStoriesTemplate);

        var messages = new List<ChatMessageDto>
        {
            new()
            {
                Role = "system",
Content = """
Bạn là BA Agent.
Bạn chỉ viết requirement, không viết code.

Hãy trả về JSON duy nhất:

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
  "userStories": {
    "content": "..."
  }
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

        var result = ParseBAResponse(response, project, userMessage);
        await GenerateDraftDocxFiles(project, ba.Id, result);

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

    private async Task GenerateDraftDocxFiles(
    Project project,
    Guid baId,
    BARequirementDocxResult result)
    {
        var root = _configuration["AgentWorkspace:RootPath"]
            ?? throw new InvalidOperationException("AgentWorkspace:RootPath is missing.");

        var projectFolder = MakeSafeFolderName(project.Name);
        var draftPath = Path.Combine(root, projectFolder, "docs", "draft");

        Directory.CreateDirectory(draftPath);

        var brdTemplate = Path.Combine(_env.ContentRootPath, "Templates", "BRD_Template.docx");
        var srsTemplate = Path.Combine(_env.ContentRootPath, "Templates", "SRS_Template.docx");

        var brdOutput = Path.Combine(draftPath, "BRD.docx");
        var srsOutput = Path.Combine(draftPath, "SRS.docx");
        var storiesOutput = Path.Combine(draftPath, "UserStories.docx");

        _docxWriter.CreateFromTemplate(
            brdTemplate,
            brdOutput,
            BuildBrdReplacements(project, result.Brd));

        _docxWriter.CreateFromTemplate(
            srsTemplate,
            srsOutput,
            BuildSrsReplacements(project, result.Srs));

        CreateSimpleUserStoriesDocx(storiesOutput, result.UserStories.Content);

        await UpsertDraftDocument(project.Id, baId, "BRD.docx", brdOutput, _docxWriter.ExtractText(brdOutput));
        await UpsertDraftDocument(project.Id, baId, "SRS.docx", srsOutput, _docxWriter.ExtractText(srsOutput));
        await UpsertDraftDocument(project.Id, baId, "UserStories.docx", storiesOutput, result.UserStories.Content);
    }

    private static Dictionary<string, string> BuildBrdReplacements(
    Project project,
    BrdDto brd)
    {
        var today = DateTime.Now.ToString("dd/MM/yyyy");

        return new Dictionary<string, string>
        {
            ["[Tên Dự Án]"] = project.Name,
            ["[Tên Dự Án / Project Name]"] = project.Name,
            ["[Tên dự án]"] = project.Name,
            ["[DD/MM/YYYY]"] = today,

            ["[Viết 3–5 câu ngắn gọn cho cấp lãnh đạo: vấn đề kinh doanh là gì, giải pháp đề xuất, lợi ích kỳ vọng và mức độ ưu tiên. Phần này đọc độc lập mà không cần đọc toàn bộ tài liệu.]"]
                = brd.ExecutiveSummary,

            ["[Mô tả tình hình hiện tại của tổ chức/thị trường dẫn đến sự cần thiết của dự án này. Đề cập đến xu hướng ngành, áp lực cạnh tranh, hoặc thay đổi nội bộ nếu có.]"]
                = brd.BusinessContext,

            ["[Mô tả rõ ràng vấn đề đang gặp phải. Trả lời: Ai đang gặp vấn đề? Vấn đề xảy ra ở đâu/khi nào? Tác động của vấn đề là gì?]"]
                = brd.ProblemStatement,

            ["[Liên kết dự án với chiến lược tổ chức: mục tiêu này hỗ trợ OKR/KPI nào của công ty?]"]
                = brd.BusinessObjectives,

            ["[Liệt kê rõ ràng những gì DỰ ÁN NÀY bao gồm — quy trình, hệ thống, phòng ban, địa lý...]"]
                = brd.InScope,

            ["[Liệt kê rõ ràng những gì KHÔNG thuộc dự án này để tránh scope creep.]"]
                = brd.OutOfScope,

            ["[Mô tả quy trình hiện tại từng bước. Xác định các điểm đau (pain points), nút thắt cổ chai (bottleneck), bước thủ công không hiệu quả. Có thể đính kèm sơ đồ BPMN/flowchart.]"]
                = brd.AsIsProcess,

            ["[Mô tả quy trình mới sau khi dự án hoàn thành. Làm nổi bật sự khác biệt so với AS-IS và lợi ích mang lại.]"]
                = brd.ToBeProcess
        };
    }

    private static void CreateSimpleUserStoriesDocx(string outputPath, string content)
    {
        using var doc = WordprocessingDocument.Create(
            outputPath,
            WordprocessingDocumentType.Document);

        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());

        var body = mainPart.Document.Body!;

        body.AppendChild(new Paragraph(
            new Run(new Text("User Stories"))));

        foreach (var line in content.Split('\n'))
        {
            body.AppendChild(new Paragraph(
                new Run(new Text(line))));
        }

        mainPart.Document.Save();
    }

    private async Task UpsertDraftDocument(
    Guid projectId,
    Guid agentId,
    string fileName,
    string filePath,
    string previewContent)
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
                FilePath = filePath,
                Content = previewContent,
                TokenUsed = EstimateTokens(previewContent)
            });
        }
        else
        {
            doc.Content = previewContent;
            doc.FilePath = filePath;
            doc.TokenUsed = EstimateTokens(previewContent);
            doc.CreatedAt = DateTime.UtcNow;
        }
    }

    private static Dictionary<string, string> BuildSrsReplacements(
    Project project,
    SrsDto srs)
    {
        var today = DateTime.Now.ToString("dd/MM/yyyy");

        return new Dictionary<string, string>
        {
            ["[Tên Dự Án]"] = project.Name,
            ["[Tên Dự Án / Project Name]"] = project.Name,
            ["[Tên dự án]"] = project.Name,
            ["[DD/MM/YYYY]"] = today,

            ["[Mô tả mục đích của tài liệu SRS này. Tài liệu này xác định các yêu cầu phần mềm cho hệ thống XYZ nhằm phục vụ...]"]
                = srs.Purpose,

            ["[Mô tả phạm vi của hệ thống. Hệ thống sẽ làm gì và không làm gì? Giá trị mang lại là gì?]"]
                = srs.Scope,

            ["[Mô tả sản phẩm ở cấp độ cao. Hệ thống mới hay là một phần của hệ thống lớn hơn? Các thành phần chính gồm những gì?]"]
                = srs.Scope,

            ["[Nền tảng/hệ điều hành được hỗ trợ]"]
                = srs.Assumptions,

            ["[Ràng buộc công nghệ: ngôn ngữ lập trình, framework]"]
                = srs.Constraints,

            ["[Mô tả sơ đồ ERD hoặc liệt kê các entity chính và quan hệ giữa chúng. Đính kèm sơ đồ nếu có.]"]
                = srs.DataRequirements
        };
    }

    private static string BuildBAPrompt(
     Project project,
     string userMessage,
     string currentBrd,
     string currentSrs,
     string currentStories,
     string brdTemplate,
     string srsTemplate,
     string userStoriesTemplate)
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}

User latest message:
{{userMessage}}

Current BRD draft:
{{currentBrd}}

Current SRS draft:
{{currentSrs}}

Current UserStories draft:
{{currentStories}}

Company BRD Template:
{{brdTemplate}}

Company SRS Template:
{{srsTemplate}}

Company UserStories Template:
{{userStoriesTemplate}}

Your task:
- Update BRD.md based on Company BRD Template.
- Update SRS.md based on Company SRS Template.
- Update UserStories.md based on Company UserStories Template.
- Keep the same section order as the templates.
- Fill unknown sections with "TBD" or "Cần làm rõ".
- Ask user for missing important information in assistantMessage.
- Do NOT write source code.
- Do NOT generate implementation files.
- Do NOT call tools.
- Return JSON only.

Output format:
{
  "assistantMessage": "...",
  "brd": "...markdown...",
  "srs": "...markdown...",
  "userStories": "...markdown..."
}
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

    private static BARequirementDocxResult ParseBAResponse(
        string response,
        Project project,
        string userMessage)
    {
        try
        {
            var json = JsonExtractor.Extract(response);

            var result = JsonSerializer.Deserialize<BARequirementDocxResult>(
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

        return new BARequirementDocxResult
        {
            AssistantMessage = "Tôi đã cập nhật requirement draft dựa trên thông tin bạn cung cấp.",
            Brd = new BrdDto
            {
                ProjectName = project.Name,
                ExecutiveSummary = userMessage,
                BusinessContext = "Cần làm rõ",
                ProblemStatement = userMessage,
                BusinessObjectives = "Cần làm rõ",
                InScope = userMessage,
                OutOfScope = "Cần làm rõ",
                Stakeholders = "Cần làm rõ",
                BusinessRequirements = userMessage,
                AsIsProcess = "Cần làm rõ",
                ToBeProcess = "Cần làm rõ",
                Risks = "Cần làm rõ",
                OpenQuestions = "Cần làm rõ"
            },
            Srs = new SrsDto
            {
                ProjectName = project.Name,
                Purpose = userMessage,
                Scope = userMessage,
                UserGroups = "Cần làm rõ",
                Assumptions = "Cần làm rõ",
                Constraints = "Cần làm rõ",
                FunctionalRequirements = userMessage,
                NonFunctionalRequirements = "Cần làm rõ",
                UiRequirements = "Cần làm rõ",
                ApiRequirements = "Cần làm rõ",
                DataRequirements = "Cần làm rõ",
                DeploymentRequirements = "Cần làm rõ",
                TestingRequirements = "Cần làm rõ",
                OpenIssues = "Cần làm rõ"
            },
            UserStories = new UserStoriesDto
            {
                Content = $"""
# User Stories

## US-001
As a user,
I want {userMessage},
so that I can achieve my business goal.

Acceptance Criteria:
- Given the user has access to the system
- When the user performs the required action
- Then the system should respond correctly
"""
            }
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
