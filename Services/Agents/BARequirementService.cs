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

        var currentBrd = GetDoc(project, "BRD.docx");
        var currentSrs = GetDoc(project, "SRS.docx");
        var currentFsd = GetDoc(project, "FSD.docx");
        var currentStories = GetDoc(project, "UserStories.docx");
        var currentAiDesignSpec = GetDoc(project, "AIDesignSpec.docx");
        var prompt = BuildBAPrompt(
            project,
            userMessage,
            currentBrd,
            currentSrs,
            currentFsd,
            currentStories,
            currentAiDesignSpec,
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
5. Không được viết source code.
6. Không được build/run/test code.
7. Không được đóng vai Developer.
8. Nếu thiếu thông tin quan trọng, hãy ghi vào openQuestions và assistantMessage.

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
  "userStories": {
    "content": "..."
  },
  "aiDesignSpec": {
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

    private async Task GenerateDraftDocxFiles(Project project, Guid baId, BARequirementDocxResult result)
    {
        var root = _configuration["AgentWorkspace:RootPath"]
            ?? throw new InvalidOperationException("AgentWorkspace:RootPath is missing.");

        var projectFolder = MakeSafeFolderName(project.Name);
        var draftPath = Path.Combine(root, projectFolder, "docs", "draft");

        Directory.CreateDirectory(draftPath);

        var brdTemplate = _templateService.EnsureTemplateDocx("BRD_Template.docx");
        var srsTemplate = _templateService.EnsureTemplateDocx("SRS_Template.docx");
        var fsdTemplate = _templateService.EnsureTemplateDocx("FSD_Template.docx");

        var brdOutput = Path.Combine(draftPath, "BRD.docx");
        var srsOutput = Path.Combine(draftPath, "SRS.docx");
        var fsdOutput = Path.Combine(draftPath, "FSD.docx");
        var storiesOutput = Path.Combine(draftPath, "UserStories.docx");
        var aiDesignSpecOutput = Path.Combine(draftPath, "AIDesignSpec.docx");

        _docxWriter.CreateFromTemplate(brdTemplate, brdOutput, BuildBrdReplacements(project, result.Brd));
        _docxWriter.CreateFromTemplate(srsTemplate, srsOutput, BuildSrsReplacements(project, result.Srs));
        _docxWriter.CreateFromTemplate(fsdTemplate, fsdOutput, BuildFsdReplacements(project, result.Fsd));
        CreateSimpleDocumentDocx(storiesOutput, "User Stories", result.UserStories.Content);
        CreateSimpleDocumentDocx(aiDesignSpecOutput, "AI Design Spec", result.AiDesignSpec.Content);

        await UpsertDraftDocument(project.Id, baId, "BRD.docx", brdOutput, _docxWriter.ExtractText(brdOutput));
        await UpsertDraftDocument(project.Id, baId, "SRS.docx", srsOutput, _docxWriter.ExtractText(srsOutput));
        await UpsertDraftDocument(project.Id, baId, "FSD.docx", fsdOutput, _docxWriter.ExtractText(fsdOutput));
        await UpsertDraftDocument(project.Id, baId, "UserStories.docx", storiesOutput, result.UserStories.Content);
        await UpsertDraftDocument(project.Id, baId, "AIDesignSpec.docx", aiDesignSpecOutput, result.AiDesignSpec.Content);
    }

    private static void CreateSimpleDocumentDocx(string outputPath, string title, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var doc = WordprocessingDocument.Create(
            outputPath,
            WordprocessingDocumentType.Document);

        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());

        var body = mainPart.Document.Body!;

        body.AppendChild(new Paragraph(
            new Run(new Text(title))));

        foreach (var line in content.Split('\n'))
        {
            body.AppendChild(new Paragraph(
                new Run(new Text(line))));
        }

        mainPart.Document.Save();
    }

    private async Task UpsertDraftDocument(Guid projectId, Guid agentId, string fileName, string filePath, string previewContent)
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

    private static Dictionary<string, string> BuildBrdReplacements(Project project, BrdDto brd)
    {
        var today = DateTime.Now.ToString("dd/MM/yyyy");

        return new Dictionary<string, string>
        {
            ["[Tên Dự Án]"] = project.Name,
            ["[Tên Dự Án / Project Name]"] = project.Name,
            ["[Tên dự án]"] = project.Name,
            ["[DD/MM/YYYY]"] = today,
            ["[Phòng ban / Bộ phận]"] = "TBD",
            ["[Tên & Chức danh]"] = "BA Agent",
            ["[Tên tác giả]"] = "BA Agent",
            ["[Tên]"] = "BA Agent",
            ["[Viết 3–5 câu ngắn gọn cho cấp lãnh đạo: vấn đề kinh doanh là gì, giải pháp đề xuất, lợi ích kỳ vọng và mức độ ưu tiên. Phần này đọc độc lập mà không cần đọc toàn bộ tài liệu.]"] = brd.ExecutiveSummary,
            ["[Mô tả tình hình hiện tại của tổ chức/thị trường dẫn đến sự cần thiết của dự án này. Đề cập đến xu hướng ngành, áp lực cạnh tranh, hoặc thay đổi nội bộ nếu có.]"] = brd.BusinessContext,
            ["[Mô tả rõ ràng vấn đề đang gặp phải. Trả lời: Ai đang gặp vấn đề? Vấn đề xảy ra ở đâu/khi nào? Tác động của vấn đề là gì?]"] = brd.ProblemStatement,
            ["[Liên kết dự án với chiến lược tổ chức: mục tiêu này hỗ trợ OKR/KPI nào của công ty?]"] = brd.BusinessObjectives,
            ["[Liệt kê rõ ràng những gì DỰ ÁN NÀY bao gồm — quy trình, hệ thống, phòng ban, địa lý...]"] = brd.InScope,
            ["[Liệt kê rõ ràng những gì KHÔNG thuộc dự án này để tránh scope creep.]"] = brd.OutOfScope,
            ["[Mô tả quy trình hiện tại từng bước. Xác định các điểm đau (pain points), nút thắt cổ chai (bottleneck), bước thủ công không hiệu quả. Có thể đính kèm sơ đồ BPMN/flowchart.]"] = brd.AsIsProcess,
            ["[Mô tả quy trình mới sau khi dự án hoàn thành. Làm nổi bật sự khác biệt so với AS-IS và lợi ích mang lại.]"] = brd.ToBeProcess,
            ["[Mô tả rủi ro]"] = brd.Risks,
            ["[Vấn đề cần làm rõ]"] = brd.OpenQuestions
        };
    }

    private static Dictionary<string, string> BuildSrsReplacements(Project project, SrsDto srs)
    {
        var today = DateTime.Now.ToString("dd/MM/yyyy");

        return new Dictionary<string, string>
        {
            ["[Tên Dự Án]"] = project.Name,
            ["[Tên Dự Án / Project Name]"] = project.Name,
            ["[Tên dự án]"] = project.Name,
            ["[DD/MM/YYYY]"] = today,
            ["[Tên tác giả]"] = "BA Agent",
            ["[Tên người phê duyệt]"] = "TBD",
            ["[Tên]"] = "BA Agent",
            ["[Mô tả mục đích của tài liệu SRS này. Tài liệu này xác định các yêu cầu phần mềm cho hệ thống XYZ nhằm phục vụ...]"] = srs.Purpose,
            ["[Mô tả phạm vi của hệ thống. Hệ thống sẽ làm gì và không làm gì? Giá trị mang lại là gì?]"] = srs.Scope,
            ["[Mô tả sản phẩm ở cấp độ cao. Hệ thống mới hay là một phần của hệ thống lớn hơn? Các thành phần chính gồm những gì?]"] = srs.Scope,
            ["[Nền tảng/hệ điều hành được hỗ trợ]"] = srs.Assumptions,
            ["[Ràng buộc công nghệ: ngôn ngữ lập trình, framework]"] = srs.Constraints,
            ["[Mô tả sơ đồ ERD hoặc liệt kê các entity chính và quan hệ giữa chúng. Đính kèm sơ đồ nếu có.]"] = srs.DataRequirements,
            ["[Yêu cầu bảo mật khác]"] = srs.NonFunctionalRequirements,
            ["[Yêu cầu hiệu năng khác]"] = srs.NonFunctionalRequirements,
            ["[Danh sách màn hình/trang chính]"] = srs.UiRequirements,
            ["[METHOD]"] = "TBD",
            ["[/endpoint]"] = srs.ApiRequirements,
            ["[Mô tả]"] = srs.FunctionalRequirements,
            ["[Ghi chú]"] = srs.OpenIssues
        };
    }

    private static Dictionary<string, string> BuildFsdReplacements(Project project, FsdDto fsd)
    {
        var today = DateTime.Now.ToString("dd/MM/yyyy");

        return new Dictionary<string, string>
        {
            ["[Tên Dự Án]"] = project.Name,
            ["[Tên Dự Án / Project Name]"] = project.Name,
            ["[Tên dự án]"] = project.Name,
            ["[DD/MM/YYYY]"] = today,
            ["[Module hoặc toàn hệ thống]"] = string.IsNullOrWhiteSpace(fsd.ModuleScope) ? "Toàn hệ thống" : fsd.ModuleScope,
            ["[Tên & Chức danh]"] = "BA Agent",
            ["[Tên]"] = "BA Agent",
            ["[FSD (Functional Specification Document) là tài liệu cầu nối giữa BRD (Business Requirements) và SRS/TDD (Technical Specification). FSD mô tả CHI TIẾT HÀNH VI của hệ thống từ góc nhìn người dùng — hệ thống làm gì trong từng tình huống, không mô tả cách implement kỹ thuật.]"] = fsd.Purpose,
            ["[Tài liệu này bao gồm các module/chức năng nào? Liệt kê rõ những gì có và không có trong FSD này.]"] = fsd.Scope,
            ["[Mô tả cấu trúc phân cấp của hệ thống: các Module lớn → Sub-module → Chức năng. Đây là bản đồ tổng quan để người đọc hiểu FSD trước khi đọc chi tiết từng chức năng. Đính kèm sơ đồ nếu có.]"] = fsd.FunctionalArchitecture,
            ["[Module]"] = fsd.NavigationStructure,
            ["[Sub-module]"] = fsd.ScreenList,
            ["[Chức năng]"] = fsd.FeatureDetails,
            ["[FT-XXX]"] = fsd.FeatureDetails,
            ["[Actor]"] = fsd.Actors,
            ["[Quyền]"] = fsd.Actors,
            ["[Danh sách màn hình/trang chính]"] = fsd.ScreenList,
            ["[Mô tả]"] = fsd.UiSpecification,
            ["[Ghi chú]"] = fsd.OpenQuestions,
            ["[METHOD]"] = "TBD",
            ["[/path]"] = fsd.ApiReferences,
            ["[Entity name]"] = fsd.DataReferences,
            ["[BR-XXX: Quy tắc nghiệp vụ áp dụng]"] = fsd.BusinessRules,
            ["[Hành động người dùng/hệ thống]"] = fsd.MainFlows,
            ["[Khi nào luồng này xảy ra?]"] = fsd.AlternativeFlows
        };
    }

    private static string BuildBAPrompt(
     Project project,
     string userMessage,
     string currentBrd,
     string currentSrs,
     string currentFsd,
     string currentStories,
     string currentAiDesignSpec,
     string brdTemplate,
     string srsTemplate,
     string fsdTemplate,
     string userStoriesTemplate)
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}

User latest message:
{{userMessage}}

Current BRD preview:
{{currentBrd}}

Current SRS preview:
{{currentSrs}}

Current FSD preview:
{{currentFsd}}

Current UserStories preview:
{{currentStories}}

Current AI Design Spec preview:
{{currentAiDesignSpec}}

Company BRD Template:
{{brdTemplate}}

Company SRS Template:
{{srsTemplate}}

Company FSD Template:
{{fsdTemplate}}

Company UserStories Template:
{{userStoriesTemplate}}

Your task:
- Update BRD.docx structured data based on Company BRD Template.
- Update SRS.docx structured data based on Company SRS Template.
- Update FSD.docx structured data based on Company FSD Template.
- Update UserStories.docx content.
- Generate or update AIDesignSpec.docx content.

FSD rules:
- FSD must focus on functional behavior.
- Include navigation structure.
- Include screen hierarchy.
- Include feature details.
- Include actors and permissions.
- Include main flows and alternative flows.
- Include UI/API/Data references.
- Do not describe low-level implementation.

AI Design Spec rules:
- AIDesignSpec is the ONLY document that will be sent to Developer Agent after approval.
- It must be compact, clear, and optimized for AI code generation.
- It must include enough information to generate a POC/mockup.
- It should summarize BRD/SRS/FSD/UserStories into developer-ready context.
- Do not include unnecessary business background.
- Do not include long legal/project management sections.

AIDesignSpec must include these sections:

# AI Design Spec

## 1. Project Goal
Short summary of what the system must achieve.

## 2. Target Users / Actors
List user roles and what they can do.

## 3. MVP Scope
What must be built in the POC.

## 4. Out of Scope
What should not be built now.

## 5. Navigation Structure
Sidebar / top menu / child tabs.

Example:
- Projects
  - Master List
  - Training Plan
  - Implementation
  - Training Calendar
- Reports
- Settings
  - Training Catalog
  - System Settings

## 6. Screens To Generate
For each screen:
- Screen name
- Route URL
- Purpose
- Main components
- Table columns if any
- Form fields if any
- Buttons/actions
- Validation rules
- Empty/loading/error states

## 7. UI/UX Direction
Describe visual style:
- Enterprise dashboard
- Left sidebar
- Cards
- Tables
- Modal create/edit
- Status badges
- Responsive behavior

## 8. Data Model Summary
List main entities and important fields.

## 9. API Expectations
List expected endpoints at high level.
Do not over-engineer.

## 10. Business Rules
Only rules required for POC behavior.

## 11. Developer Instructions
Tell Developer Agent:
- Generate clean source code.
- Prioritize working POC.
- Use simple architecture.
- Build only MVP scope.
- Do not generate unnecessary modules.
- Run build/test if tools are available.

General rules:
- Keep the same section order as the templates.
- Fill unknown sections with "TBD" or "Cần làm rõ".
- Ask user for missing important information in assistantMessage.
- Do NOT write source code.
- Do NOT generate implementation files.
- Do NOT call tools.
- Return JSON only.
""";
    }

    private static BARequirementDocxResult ParseBAResponse(string response, Project project, string userMessage)
    {
        try
        {
            var json = JsonExtractor.Extract(response);
            var result = JsonSerializer.Deserialize<BARequirementDocxResult>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result != null)
                return result;
        }
        catch
        {
            // fallback below
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
            Fsd = new FsdDto
            {
                ProjectName = project.Name,
                ModuleScope = "Toàn hệ thống",
                Purpose = "FSD mô tả chi tiết hành vi chức năng của hệ thống dựa trên requirement đã thu thập.",
                Scope = userMessage,
                FunctionalArchitecture = userMessage,
                Actors = "Cần làm rõ",
                NavigationStructure = "Cần làm rõ",
                ScreenList = "Cần làm rõ",
                UiSpecification = "Cần làm rõ",
                FeatureDetails = userMessage,
                BusinessRules = "Cần làm rõ",
                MainFlows = "Cần làm rõ",
                AlternativeFlows = "Cần làm rõ",
                ApiReferences = "Cần làm rõ",
                DataReferences = "Cần làm rõ",
                OpenQuestions = "Cần làm rõ"
            },
            UserStories = new UserStoriesDto
            {
                Content = $$"""
# User Stories

## US-001
As a user,
I want {{userMessage}},
so that I can achieve my business goal.

Acceptance Criteria:
- Given the user has access to the system
- When the user performs the required action
- Then the system should respond correctly
"""
            }
        };
    }

    private static int EstimateTokens(string? text)
        => string.IsNullOrWhiteSpace(text) ? 0 : Math.Max(1, text.Length / 4);

    private static string MakeSafeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        return name.Replace(" ", "-").ToLowerInvariant();
    }
}

public class BARequirementDocxResult
{
    public string AssistantMessage { get; set; } = "";
    public BrdDto Brd { get; set; } = new();
    public SrsDto Srs { get; set; } = new();
    public FsdDto Fsd { get; set; } = new();
    public UserStoriesDto UserStories { get; set; } = new();
    public AiDesignSpecDto AiDesignSpec { get; set; } = new();
}

public class BrdDto
{
    public string ProjectName { get; set; } = "";
    public string ExecutiveSummary { get; set; } = "";
    public string BusinessContext { get; set; } = "";
    public string ProblemStatement { get; set; } = "";
    public string BusinessObjectives { get; set; } = "";
    public string InScope { get; set; } = "";
    public string OutOfScope { get; set; } = "";
    public string Stakeholders { get; set; } = "";
    public string BusinessRequirements { get; set; } = "";
    public string AsIsProcess { get; set; } = "";
    public string ToBeProcess { get; set; } = "";
    public string Risks { get; set; } = "";
    public string OpenQuestions { get; set; } = "";
}

public class SrsDto
{
    public string ProjectName { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Scope { get; set; } = "";
    public string UserGroups { get; set; } = "";
    public string Assumptions { get; set; } = "";
    public string Constraints { get; set; } = "";
    public string FunctionalRequirements { get; set; } = "";
    public string NonFunctionalRequirements { get; set; } = "";
    public string UiRequirements { get; set; } = "";
    public string ApiRequirements { get; set; } = "";
    public string DataRequirements { get; set; } = "";
    public string DeploymentRequirements { get; set; } = "";
    public string TestingRequirements { get; set; } = "";
    public string OpenIssues { get; set; } = "";
}

public class FsdDto
{
    public string ProjectName { get; set; } = "";
    public string ModuleScope { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Scope { get; set; } = "";
    public string FunctionalArchitecture { get; set; } = "";
    public string Actors { get; set; } = "";
    public string NavigationStructure { get; set; } = "";
    public string ScreenList { get; set; } = "";
    public string UiSpecification { get; set; } = "";
    public string FeatureDetails { get; set; } = "";
    public string BusinessRules { get; set; } = "";
    public string MainFlows { get; set; } = "";
    public string AlternativeFlows { get; set; } = "";
    public string ApiReferences { get; set; } = "";
    public string DataReferences { get; set; } = "";
    public string OpenQuestions { get; set; } = "";
}

public class UserStoriesDto
{
    public string Content { get; set; } = "";
}

public class AiDesignSpecDto
{
    public string Content { get; set; } = "";
}
