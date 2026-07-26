using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Sửa thông tin dự án ở trang Projects. Điểm cốt tử: tên thư mục workspace dẫn xuất từ TÊN dự án, nên đổi
// tên phải kéo theo đổi tên thư mục — và khi không đổi được thư mục thì KHÔNG được lưu tên mới (nếu lưu,
// mọi đường dẫn tính lại sẽ trỏ vào thư mục trống ⇒ tài liệu/POC đã sinh coi như mất).
public class UpdateProjectUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public UpdateProjectUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "50123", DisplayName = "HcP/TEF3.3" });
        db.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "50999", DisplayName = "HcP/GONE", IsDelete = true });
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Task App",
            Description = "Old description",
            OrgUnitCode = "50123",
            CreatedByUsername = "alice"
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesTrimmedFields_AndRenamesWorkspace()
    {
        var storage = new FakeArtifactStorage();
        await using var db = NewDb();

        var result = await new UpdateProjectUseCase(db, storage, new NullAuditLogger()).ExecuteAsync(new ProjectEditVm
        {
            ProjectId = _projectId,
            Name = "  Task App v2  ",
            Description = "  New description  ",
            OrgUnitCode = " 50123 "
        });

        Assert.Equal(UpdateProjectResult.Updated, result);

        var project = await NewDb().Projects.SingleAsync(p => p.Id == _projectId);
        Assert.Equal("Task App v2", project.Name);
        Assert.Equal("New description", project.Description);
        Assert.Equal("50123", project.OrgUnitCode);

        Assert.Equal(
            (WorkspacePathResolver.GetWorkspaceFolder(_projectId, "Task App"),
             WorkspacePathResolver.GetWorkspaceFolder(_projectId, "Task App v2")),
            Assert.Single(storage.Renames));
    }

    [Fact]
    public async Task ExecuteAsync_WhenOnlyDescriptionChanges_DoesNotTouchWorkspace()
    {
        var storage = new FakeArtifactStorage();
        await using var db = NewDb();

        var result = await NewSut(db, storage).ExecuteAsync(new ProjectEditVm
        {
            ProjectId = _projectId,
            Name = "Task App",
            Description = "Only the description moved",
            OrgUnitCode = "50123"
        });

        Assert.Equal(UpdateProjectResult.Updated, result);
        Assert.Empty(storage.Renames);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNothingChanges_ReturnsNoChange()
    {
        var storage = new FakeArtifactStorage();
        await using var db = NewDb();

        var result = await NewSut(db, storage).ExecuteAsync(new ProjectEditVm
        {
            ProjectId = _projectId,
            Name = "Task App",
            Description = "Old description",
            OrgUnitCode = "50123"
        });

        Assert.Equal(UpdateProjectResult.NoChange, result);
        Assert.Empty(storage.Renames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_WithBlankName_ReturnsNameRequired_AndKeepsProject(string name)
    {
        await using var db = NewDb();

        var result = await NewSut(db).ExecuteAsync(new ProjectEditVm { ProjectId = _projectId, Name = name });

        Assert.Equal(UpdateProjectResult.NameRequired, result);
        Assert.Equal("Task App", (await NewDb().Projects.SingleAsync(p => p.Id == _projectId)).Name);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownProject_ReturnsProjectNotFound()
    {
        await using var db = NewDb();

        var result = await NewSut(db).ExecuteAsync(new ProjectEditVm { ProjectId = Guid.NewGuid(), Name = "Whatever" });

        Assert.Equal(UpdateProjectResult.ProjectNotFound, result);
    }

    // Cùng luật với CreateProjectUseCase: mã đơn vị lạ/đã xóa mềm coi như "không chọn", không chặn lần lưu.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("60000")]
    [InlineData("50999")]
    public async Task ExecuteAsync_WithMissingOrUnknownOrgUnit_ClearsCode(string? code)
    {
        await using var db = NewDb();

        var result = await NewSut(db).ExecuteAsync(new ProjectEditVm
        {
            ProjectId = _projectId,
            Name = "Task App",
            Description = "Old description",
            OrgUnitCode = code
        });

        Assert.Equal(UpdateProjectResult.Updated, result);
        Assert.Null((await NewDb().Projects.SingleAsync(p => p.Id == _projectId)).OrgUnitCode);
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Queued)]
    [InlineData(WorkflowRunStatus.Running)]
    [InlineData(WorkflowRunStatus.WaitingForHuman)]
    public async Task ExecuteAsync_WhenWorkflowStillRunning_BlocksRename(WorkflowRunStatus status)
    {
        await AddWorkflowRunAsync(status);
        var storage = new FakeArtifactStorage();
        await using var db = NewDb();

        var result = await NewSut(db, storage).ExecuteAsync(new ProjectEditVm
        {
            ProjectId = _projectId,
            Name = "Renamed mid-flight",
            Description = "Old description",
            OrgUnitCode = "50123"
        });

        Assert.Equal(UpdateProjectResult.RenameBlockedByRunningWorkflow, result);
        Assert.Empty(storage.Renames);
        Assert.Equal("Task App", (await NewDb().Projects.SingleAsync(p => p.Id == _projectId)).Name);
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Completed)]
    [InlineData(WorkflowRunStatus.Failed)]
    [InlineData(WorkflowRunStatus.Canceled)]
    public async Task ExecuteAsync_WhenWorkflowFinished_AllowsRename(WorkflowRunStatus status)
    {
        await AddWorkflowRunAsync(status);
        await using var db = NewDb();

        var result = await NewSut(db).ExecuteAsync(new ProjectEditVm
        {
            ProjectId = _projectId,
            Name = "Renamed after the run",
            Description = "Old description",
            OrgUnitCode = "50123"
        });

        Assert.Equal(UpdateProjectResult.Updated, result);
    }

    // Đang chạy workflow vẫn phải sửa được mô tả/đơn vị — chỉ TÊN bị khóa.
    [Fact]
    public async Task ExecuteAsync_WhenWorkflowRunning_StillUpdatesDescription()
    {
        await AddWorkflowRunAsync(WorkflowRunStatus.Running);
        await using var db = NewDb();

        var result = await NewSut(db).ExecuteAsync(new ProjectEditVm
        {
            ProjectId = _projectId,
            Name = "Task App",
            Description = "Fixed a typo while the pipeline runs",
            OrgUnitCode = "50123"
        });

        Assert.Equal(UpdateProjectResult.Updated, result);
        Assert.Equal("Fixed a typo while the pipeline runs",
            (await NewDb().Projects.SingleAsync(p => p.Id == _projectId)).Description);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkspaceRenameFails_KeepsEverythingUnchanged()
    {
        var storage = new FakeArtifactStorage { RenameSucceeds = false };
        await using var db = NewDb();

        var result = await NewSut(db, storage).ExecuteAsync(new ProjectEditVm
        {
            ProjectId = _projectId,
            Name = "Renamed",
            Description = "Also changed",
            OrgUnitCode = "50123"
        });

        Assert.Equal(UpdateProjectResult.WorkspaceRenameFailed, result);

        var project = await NewDb().Projects.SingleAsync(p => p.Id == _projectId);
        Assert.Equal("Task App", project.Name);
        Assert.Equal("Old description", project.Description);
    }

    private async Task AddWorkflowRunAsync(WorkflowRunStatus status)
    {
        await using var db = NewDb();
        db.WorkflowRuns.Add(new WorkflowRun { ProjectId = _projectId, Status = status });
        await db.SaveChangesAsync();
    }

    private static UpdateProjectUseCase NewSut(AppDbContext db, IArtifactStorage? storage = null) =>
        new(db, storage ?? new FakeArtifactStorage(), new NullAuditLogger());

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeArtifactStorage : IArtifactStorage
    {
        public bool RenameSucceeds { get; init; } = true;
        public List<(string OldKey, string NewKey)> Renames { get; } = new();

        public void InitializeProjectWorkspace(string projectKey) { }

        public bool TryRenameProjectWorkspace(string oldProjectKey, string newProjectKey)
        {
            Renames.Add((oldProjectKey, newProjectKey));
            return RenameSucceeds;
        }

        public string GetDraftPath(string projectKey, ProjectArtifactDescriptor artifact) => Path.Combine(Path.GetTempPath(), artifact.FileName);
        public string GetVersionPath(string projectKey, string versionName, ProjectArtifactDescriptor artifact) => Path.Combine(Path.GetTempPath(), versionName, artifact.FileName);
        public string GetSourceUploadDir(string projectKey) => Path.GetTempPath();
    }

    private sealed class NullAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditCategory category, AuditAction action, string entityId, string summary,
            object? before = null, object? after = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
