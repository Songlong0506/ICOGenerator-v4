using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Requirements.Templates;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Trang bìa Product Brief in nhãn phiên bản lúc SINH file. Approve move nguyên thư mục draft sang V{n},
// nên nếu không dựng lại file thì bản đã duyệt vẫn ghi "Bản nháp (chưa duyệt)" trên bìa.
public class ApproveRequirementBriefVersionStampTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _workspaceRoot = Path.Combine(Path.GetTempPath(), "ico-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly WorkspacePathResolver _resolver;

    public ApproveRequirementBriefVersionStampTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _workspaceRoot })
            .Build();
        _resolver = new WorkspacePathResolver(config);

        var project = new Project { Id = _projectId, Name = "Locker" };
        var draftPath = Path.Combine(
            _resolver.GetPhaseDraftPath(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name), "01_Requirement"),
            "ProductBrief.docx");
        const string content = "# Ứng dụng quản lý tủ locker\n\n## Sản phẩm này là gì?\n\nMô tả.";
        RequirementDocumentGenerator.RenderProductBriefDocx(draftPath, project, "draft", content);

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "m" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });
        db.Projects.Add(project);
        db.ProjectDocuments.Add(new ProjectDocument
        {
            ProjectId = _projectId,
            VersionName = "draft",
            IsApproved = false,
            Folder = "01_Requirement",
            FileName = "ProductBrief.docx",
            FilePath = draftPath,
            Content = content
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_RestampsBriefCover_WithApprovedVersion()
    {
        await using (var db = NewDb())
            Assert.Equal(ApproveRequirementResult.Approved, await NewSut(db).ExecuteAsync(_projectId));

        await using var verify = NewDb();
        var brief = await verify.ProjectDocuments.SingleAsync(d => d.ProjectId == _projectId);
        Assert.Equal("V1", brief.VersionName);
        Assert.True(File.Exists(brief.FilePath));

        var text = new DocxTemplateWriter().ExtractText(brief.FilePath);
        Assert.Contains("V1 (đã duyệt)", text);
        Assert.DoesNotContain("Bản nháp (chưa duyệt)", text);
        Assert.Contains("Sản phẩm này là gì?", text);
    }

    private ApproveRequirementUseCase NewSut(AppDbContext db) =>
        new(db, _resolver, new ProjectArtifactCatalog(), new FakeOrchestrator(), NullLogger<ApproveRequirementUseCase>.Instance);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_workspaceRoot, true); } catch { /* dọn tạm, lỗi bỏ qua */ }
    }

    private sealed class FakeOrchestrator : IWorkflowOrchestrator
    {
        public Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId, bool coalesceWithActiveRun = false) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string v, string s) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> StartAiDesignSpecWorkflowAsync(Guid projectId, string v) => Task.FromResult(Guid.NewGuid());
    }
}
