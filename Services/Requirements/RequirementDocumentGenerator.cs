using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Requirements.Templates;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

public class RequirementDocumentGenerator
{
    private readonly AppDbContext _db;
    private readonly RequirementTemplateService _templateService;
    private readonly DocxTemplateWriter _docxWriter;
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly IProjectArtifactCatalog _artifactCatalog;
    private readonly IArtifactStorage _artifactStorage;

    public RequirementDocumentGenerator(
        AppDbContext db,
        RequirementTemplateService templateService,
        DocxTemplateWriter docxWriter,
        WorkspacePathResolver workspacePathResolver,
        IProjectArtifactCatalog artifactCatalog,
        IArtifactStorage artifactStorage)
    {
        _db = db;
        _templateService = templateService;
        _docxWriter = docxWriter;
        _workspacePathResolver = workspacePathResolver;
        _artifactCatalog = artifactCatalog;
        _artifactStorage = artifactStorage;
    }

    // Lượt "Write Requirement" phía user: chỉ sinh Product Brief (cho user) ở dạng draft. AI Design Spec
    // được sinh ở bước Approve (GenerateAiDesignSpecVersionFile). Tài liệu kỹ thuật nặng do team dev
    // trigger sau (GenerateTechnicalDocs).
    public async Task GenerateProductBriefDraftFiles(Project project, Guid baId, BAProductBriefResult result)
    {
        var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);

        var productBriefOutput = _artifactStorage.GetDraftPath(projectKey, _artifactCatalog.ProductBrief);

        CreateSimpleDocumentDocx(productBriefOutput, "Product Brief", result.ProductBrief.Content);

        await UpsertDocument(project.Id, baId, _artifactCatalog.ProductBrief, productBriefOutput, result.ProductBrief.Content, "draft", isApproved: false);
    }

    // Bước Approve: sinh AI Design Spec từ Product Brief đã duyệt. Ghi thẳng vào thư mục phiên bản đã
    // duyệt (V{n}) — không qua cổng draft — và lưu là tài liệu đã duyệt, giống luồng technical docs.
    public async Task GenerateAiDesignSpecVersionFile(Project project, Guid baId, string versionName, BAAiDesignSpecResult result)
    {
        var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);

        var aiDesignSpecOutput = _artifactStorage.GetVersionPath(projectKey, versionName, _artifactCatalog.AiDesignSpec);

        CreateSimpleDocumentDocx(aiDesignSpecOutput, "AI Design Spec", result.AiDesignSpec.Content);

        await UpsertDocument(project.Id, baId, _artifactCatalog.AiDesignSpec, aiDesignSpecOutput, result.AiDesignSpec.Content, versionName, isApproved: true);
    }

    // Lượt team dev trigger ở Agent Dashboard: sinh BRD/SRS/FSD/UserStories cho một phiên bản requirement
    // ĐÃ DUYỆT. Ghi thẳng vào thư mục phiên bản đó (không qua draft) và lưu là tài liệu đã duyệt.
    public async Task GenerateTechnicalDocs(Project project, Guid baId, string versionName, BARequirementDocxResult result)
    {
        var brdTemplate = _templateService.EnsureTemplateDocx("BRD_Template.docx");
        var srsTemplate = _templateService.EnsureTemplateDocx("SRS_Template.docx");
        var fsdTemplate = _templateService.EnsureTemplateDocx("FSD_Template.docx");

        var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);

        var brdArtifact = GetTechnicalArtifact("BRD");
        var srsArtifact = GetTechnicalArtifact("SRS");
        var fsdArtifact = GetTechnicalArtifact("FSD");
        var storiesArtifact = GetTechnicalArtifact("UserStories");

        var brdOutput = _artifactStorage.GetVersionPath(projectKey, versionName, brdArtifact);
        var srsOutput = _artifactStorage.GetVersionPath(projectKey, versionName, srsArtifact);
        var fsdOutput = _artifactStorage.GetVersionPath(projectKey, versionName, fsdArtifact);
        var storiesOutput = _artifactStorage.GetVersionPath(projectKey, versionName, storiesArtifact);

        _docxWriter.CreateFromTemplate(brdTemplate, brdOutput, BuildBrdReplacements(project, result.Brd));
        _docxWriter.CreateFromTemplate(srsTemplate, srsOutput, BuildSrsReplacements(project, result.Srs));
        _docxWriter.CreateFromTemplate(fsdTemplate, fsdOutput, BuildFsdReplacements(project, result.Fsd));
        CreateSimpleDocumentDocx(storiesOutput, "User Stories", result.UserStories.Content);

        await UpsertDocument(project.Id, baId, brdArtifact, brdOutput, _docxWriter.ExtractText(brdOutput), versionName, isApproved: true);
        await UpsertDocument(project.Id, baId, srsArtifact, srsOutput, _docxWriter.ExtractText(srsOutput), versionName, isApproved: true);
        await UpsertDocument(project.Id, baId, fsdArtifact, fsdOutput, _docxWriter.ExtractText(fsdOutput), versionName, isApproved: true);
        await UpsertDocument(project.Id, baId, storiesArtifact, storiesOutput, result.UserStories.Content, versionName, isApproved: true);
    }

    private ProjectArtifactDescriptor GetTechnicalArtifact(string key) =>
        _artifactCatalog.TechnicalDocuments.First(x => x.Key == key);

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
            new Run(new Text(DocxTemplateWriter.SanitizeXmlText(title)))));

        foreach (var line in (content ?? "").Split('\n'))
        {
            body.AppendChild(new Paragraph(
                new Run(new Text(DocxTemplateWriter.SanitizeXmlText(line)))));
        }

        mainPart.Document.Save();
    }

    private async Task UpsertDocument(Guid projectId, Guid agentId, ProjectArtifactDescriptor artifact, string filePath, string previewContent, string versionName, bool isApproved)
    {
        var fileName = artifact.FileName;
        var doc = await _db.ProjectDocuments
            .FirstOrDefaultAsync(x =>
                x.ProjectId == projectId &&
                x.VersionName == versionName &&
                x.FileName == fileName);

        if (doc == null)
        {
            _db.ProjectDocuments.Add(new ProjectDocument
            {
                ProjectId = projectId,
                AgentId = agentId,
                Folder = artifact.Phase,
                VersionName = versionName,
                IsApproved = isApproved,
                FileName = fileName,
                FilePath = filePath,
                Content = previewContent,
                TokenUsed = TokenEstimator.Estimate(previewContent)
            });
        }
        else
        {
            doc.Folder = artifact.Phase;
            doc.IsApproved = isApproved;
            doc.Content = previewContent;
            doc.FilePath = filePath;
            doc.TokenUsed = TokenEstimator.Estimate(previewContent);
            // Giữ nguyên CreatedAt: ghi đè bằng UtcNow mỗi lần cập nhật sẽ mất thời điểm tạo gốc và sai thứ tự sắp xếp theo CreatedAt (GetDoc/dashboard).
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
}
