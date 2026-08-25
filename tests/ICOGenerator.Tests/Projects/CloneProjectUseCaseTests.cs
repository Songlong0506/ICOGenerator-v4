using System.Text.Json;
using ICOGenerator.Application.Projects;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Nhân bản dự án. Bốn bất biến được chốt ở đây, tất cả đều là loại lỗi KHÔNG gãy gì lúc chạy mà chỉ lộ ra
// sau đó dưới dạng số liệu sai hoặc token bị đốt:
//   • Không chép AgentModelCallLogs (nhân đôi chi phí ở Usage/Delivery Quality) và PocShareLinks (link
//     công khai đang sống).
//   • Không chép task đang dở — AgentTaskWorker poll Status == Queued TOÀN CỤC nên một task Queued chép
//     sang sẽ được nhặt ngay và bắn lời gọi LLM thật.
//   • Đường dẫn tuyệt đối đã lưu (ProjectSourceFile.StoredPath) phải trỏ sang thư mục của BẢN SAO, nếu
//     không thì xóa file nguồn ở bản sao sẽ xóa file thật của dự án gốc.
//   • Chép đĩa hỏng ⇒ không lưu gì, để không có project nào trỏ vào một thư mục trống.
public class CloneProjectUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly Guid _sourceFileId = Guid.NewGuid();
    private readonly Guid _documentId = Guid.NewGuid();
    private readonly Guid _doneRunId = Guid.NewGuid();
    private readonly Guid _liveRunId = Guid.NewGuid();
    private readonly Guid _gateRunId = Guid.NewGuid();

    private const string ProjectName = "Quản lý nghỉ phép";
    private string SourceKey => WorkspacePathResolver.GetWorkspaceFolder(_projectId, ProjectName);

    public CloneProjectUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();

        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "test" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = _agentId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });

        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = ProjectName,
            Description = "Dự án gốc",
            CreatedByUsername = "alice",
            DomainKey = "leave-management",
            ConversationSummary = "tóm tắt",
            SummarizedTurnCount = 4,
            UserMemoryHarvestedTurnCount = 6,
            CoverageHarvestedTurnCount = 7,
            RequirementCoverageMap = "[RÕ] Vai trò",
            PermissionMatrix = "[{\"screen\":\"Leave\"}]",
            ScreenScopeMap = "[{\"screen\":\"Leave Request\"}]",
            PendingAssumptionsVersion = "V2",
            // Hai thứ phải bị reset ở bản sao.
            PocAcceptedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            PocAcceptedBy = "bob",
            ChecklistGapHarvested = false
        });

        db.ProjectSourceFiles.Add(new ProjectSourceFile
        {
            Id = _sourceFileId,
            ProjectId = _projectId,
            FileName = "quy-trinh.xlsx",
            ContentType = "text/csv",
            SizeBytes = 42,
            StoredPath = Path.Combine("/ws", SourceKey, "00_Source", _sourceFileId.ToString("N"), "quy-trinh.xlsx"),
            ExtractedText = "cột A"
        });

        // Lượt user có đính kèm (id trỏ về ProjectSourceFile) + một lượt ĐÃ ARCHIVE (global query filter
        // ArchivedAt == null sẽ giấu nó khỏi mọi truy vấn thường).
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _agentId,
            Role = "user",
            Message = "Tôi cần quản lý nghỉ phép",
            Attachments = JsonSerializer.Serialize(new[] { new ChatAttachment(_sourceFileId, "quy-trinh.xlsx", false) }),
            ReadinessVerified = true
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _agentId,
            Role = "assistant",
            Message = "lượt cũ đã archive",
            ArchivedAt = DateTime.UtcNow
        });

        var document = new ProjectDocument
        {
            Id = _documentId,
            ProjectId = _projectId,
            Folder = "01_Requirement",
            VersionName = "V1",
            FileName = "ProductBrief.docx",
            Content = "nội dung",
            FilePath = Path.Combine("/ws", SourceKey, "01_Requirement", "V1", "ProductBrief.docx")
        };
        db.ProjectDocuments.Add(document);
        db.ProjectDocumentRevisions.Add(new ProjectDocumentRevision
        {
            ProjectDocumentId = _documentId,
            RevisionNumber = 1,
            Content = "bản đầu",
            ChangeNote = "khởi tạo",
            VersionName = "V1"
        });

        // Ba run phủ ba nhánh trạng thái: đã xong, đang chạy, và đang chờ người duyệt.
        db.WorkflowRuns.Add(new WorkflowRun { Id = _doneRunId, ProjectId = _projectId, Status = WorkflowRunStatus.Completed });
        db.WorkflowRuns.Add(new WorkflowRun { Id = _liveRunId, ProjectId = _projectId, Status = WorkflowRunStatus.Running });
        db.WorkflowRuns.Add(new WorkflowRun { Id = _gateRunId, ProjectId = _projectId, Status = WorkflowRunStatus.WaitingForHuman });

        db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = _doneRunId, ProjectId = _projectId, AgentId = _agentId,
            Status = AgentTaskStatus.Completed, Title = "Xong", Output = "kết quả"
        });
        db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = _liveRunId, ProjectId = _projectId, AgentId = _agentId,
            Status = AgentTaskStatus.Queued, Title = "Đang chờ worker"
        });
        db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = _liveRunId, ProjectId = _projectId, AgentId = _agentId,
            Status = AgentTaskStatus.Running, Title = "Đang chạy"
        });

        db.PocComments.Add(new PocComment
        {
            ProjectId = _projectId, PageView = "Leave Request", ElementLabel = "Nút Gửi",
            Comment = "Thiếu xác nhận", CreatedByUsername = "bob"
        });

        db.AgentModelCallLogs.Add(new AgentModelCallLog
        {
            ProjectId = _projectId, AgentId = _agentId, AgentName = "BA", ModelId = "test",
            PromptTokens = 1000, TotalTokens = 1500
        });
        db.PocShareLinks.Add(new PocShareLink
        {
            ProjectId = _projectId, Token = "tok-1", Label = "Sếp",
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
        });

        db.SaveChanges();
    }

    [Fact]
    public async Task Full_CopiesEveryOwnedChildRow_WithFreshIds()
    {
        var clone = await CloneAsync(ProjectCloneScope.Full);

        await using var db = NewDb();
        Assert.Equal(1, await db.ProjectSourceFiles.CountAsync(f => f.ProjectId == clone));
        Assert.Equal(1, await db.ProjectDocuments.CountAsync(d => d.ProjectId == clone));
        Assert.Equal(3, await db.WorkflowRuns.CountAsync(r => r.ProjectId == clone));
        Assert.Equal(1, await db.PocComments.CountAsync(c => c.ProjectId == clone));

        // Id mới, không phải "chuyển chủ" dòng cũ sang project mới.
        Assert.NotEqual(_sourceFileId, (await db.ProjectSourceFiles.SingleAsync(f => f.ProjectId == clone)).Id);

        var document = await db.ProjectDocuments.SingleAsync(d => d.ProjectId == clone);
        Assert.NotEqual(_documentId, document.Id);
        var revision = await db.ProjectDocumentRevisions.SingleAsync(r => r.ProjectDocumentId == document.Id);
        Assert.Equal(1, revision.RevisionNumber);
        Assert.Equal("bản đầu", revision.Content);

        // Dự án gốc còn nguyên vẹn.
        Assert.Equal(1, await db.ProjectDocuments.CountAsync(d => d.ProjectId == _projectId));
    }

    [Fact]
    public async Task Full_DoesNotCopyCallLogsOrShareLinks()
    {
        var clone = await CloneAsync(ProjectCloneScope.Full);

        await using var db = NewDb();
        Assert.Equal(0, await db.AgentModelCallLogs.CountAsync(l => l.ProjectId == clone));
        Assert.Equal(0, await db.PocShareLinks.CountAsync(l => l.ProjectId == clone));
    }

    [Fact]
    public async Task Full_DropsInFlightTasks_AndCancelsTheirRun_ButKeepsTheApprovalGate()
    {
        var clone = await CloneAsync(ProjectCloneScope.Full);

        await using var db = NewDb();
        // Không một task nào ở trạng thái worker nhặt được.
        Assert.Empty(await db.AgentTasks
            .Where(t => t.ProjectId == clone && t.Status == AgentTaskStatus.Queued)
            .ToListAsync());

        var task = Assert.Single(await db.AgentTasks.Where(t => t.ProjectId == clone).ToListAsync());
        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal("kết quả", task.Output);

        var statuses = await db.WorkflowRuns.Where(r => r.ProjectId == clone)
            .Select(r => r.Status).ToListAsync();
        Assert.Contains(WorkflowRunStatus.Completed, statuses);
        Assert.Contains(WorkflowRunStatus.Canceled, statuses);        // run đang Running
        Assert.Contains(WorkflowRunStatus.WaitingForHuman, statuses); // cổng duyệt giữ nguyên
    }

    [Fact]
    public async Task RequirementOnly_KeepsTheInterview_ButCreatesNoDeliveryArtifacts()
    {
        var clone = await CloneAsync(ProjectCloneScope.RequirementOnly);

        await using var db = NewDb();
        Assert.Equal(2, await db.AgentConversations.IgnoreQueryFilters().CountAsync(c => c.ProjectId == clone));
        Assert.Equal(1, await db.ProjectSourceFiles.CountAsync(f => f.ProjectId == clone));

        Assert.Equal(0, await db.ProjectDocuments.CountAsync(d => d.ProjectId == clone));
        Assert.Equal(0, await db.WorkflowRuns.CountAsync(r => r.ProjectId == clone));
        Assert.Equal(0, await db.AgentTasks.CountAsync(t => t.ProjectId == clone));
        Assert.Equal(0, await db.PocComments.CountAsync(c => c.ProjectId == clone));

        var project = await db.Projects.SingleAsync(p => p.Id == clone);
        Assert.Equal("[{\"screen\":\"Leave\"}]", project.PermissionMatrix);
        Assert.Equal("[{\"screen\":\"Leave Request\"}]", project.ScreenScopeMap);
        // Cổng giả định trỏ tới bản spec V2 mà bản sao này không có.
        Assert.Null(project.PendingAssumptionsVersion);
    }

    [Fact]
    public async Task Clone_KeepsHarvestPointers_ButResetsAcceptanceAndLearningFlags()
    {
        var clone = await CloneAsync(ProjectCloneScope.Full);

        await using var db = NewDb();
        var project = await db.Projects.SingleAsync(p => p.Id == clone);

        // Con trỏ harvest giữ nguyên: reset về 0 sẽ bắt model chắt lọc LẠI đúng những lượt cũ.
        Assert.Equal(4, project.SummarizedTurnCount);
        Assert.Equal(6, project.UserMemoryHarvestedTurnCount);
        Assert.Equal(7, project.CoverageHarvestedTurnCount);
        Assert.Equal("tóm tắt", project.ConversationSummary);
        Assert.Equal("leave-management", project.DomainKey);

        Assert.Null(project.PocAcceptedAtUtc);
        Assert.Null(project.PocAcceptedBy);
        Assert.True(project.ChecklistGapHarvested);
        // Một ghi chú POC được chép sang, nên con trỏ phải đứng ở 1 để bản sao không rút lại đúng bài học đó.
        Assert.Equal(1, project.PocFeedbackHarvestedCount);

        Assert.Equal("carol", project.CreatedByUsername);
        Assert.Equal($"{ProjectName} (bản sao)", project.Name);
    }

    [Fact]
    public async Task Clone_ArchivedTurnsComeAlong_AndAttachmentIdsPointAtTheClonesOwnSourceFile()
    {
        var clone = await CloneAsync(ProjectCloneScope.Full);

        await using var db = NewDb();
        var turns = await db.AgentConversations.IgnoreQueryFilters()
            .Where(c => c.ProjectId == clone).ToListAsync();
        Assert.Equal(2, turns.Count);
        Assert.Contains(turns, t => t.ArchivedAt != null);

        var newSourceFileId = (await db.ProjectSourceFiles.SingleAsync(f => f.ProjectId == clone)).Id;
        var attachments = JsonSerializer.Deserialize<ChatAttachment[]>(
            turns.Single(t => t.Attachments != null).Attachments!)!;

        Assert.Equal(newSourceFileId, Assert.Single(attachments).Id);
    }

    [Fact]
    public async Task Clone_RewritesStoredAbsolutePaths_ToTheClonesOwnWorkspace()
    {
        var clone = await CloneAsync(ProjectCloneScope.Full);

        await using var db = NewDb();
        var project = await db.Projects.SingleAsync(p => p.Id == clone);
        var targetKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);

        var file = await db.ProjectSourceFiles.SingleAsync(f => f.ProjectId == clone);
        Assert.Contains(targetKey, file.StoredPath);
        Assert.DoesNotContain(SourceKey, file.StoredPath);

        var document = await db.ProjectDocuments.SingleAsync(d => d.ProjectId == clone);
        Assert.Contains(targetKey, document.FilePath!);
        Assert.DoesNotContain(SourceKey, document.FilePath!);
    }

    [Fact]
    public async Task Clone_WhenWorkspaceCopyFails_SavesNothing()
    {
        await using var db = NewDb();
        var sut = new CloneProjectUseCase(db, new FakeArtifactStorage { CopySucceeds = false }, new NullAuditLogger());

        var (result, newId) = await sut.ExecuteAsync(new CloneProjectVm { ProjectId = _projectId }, "carol");

        Assert.Equal(CloneProjectResult.WorkspaceCopyFailed, result);
        Assert.Null(newId);
        Assert.Equal(1, await NewDb().Projects.CountAsync());
    }

    [Fact]
    public async Task Clone_OnlyCopiesTheSourceFolder_WhenScopeIsRequirementOnly()
    {
        await using var db = NewDb();
        var storage = new FakeArtifactStorage();
        var sut = new CloneProjectUseCase(db, storage, new NullAuditLogger());

        await sut.ExecuteAsync(new CloneProjectVm { ProjectId = _projectId, Scope = ProjectCloneScope.RequirementOnly }, "carol");

        var copy = Assert.Single(storage.Copies);
        Assert.Equal(SourceKey, copy.SourceKey);
        Assert.Equal(new[] { "00_Source" }, copy.Folders);
        // Bản sao "chỉ phần yêu cầu" vẫn cần bộ khung 5 giai đoạn như một project mới tạo.
        Assert.Contains(copy.TargetKey, storage.Initialized);
    }

    [Fact]
    public async Task Clone_CopiesTheWholeTree_WhenScopeIsFull()
    {
        await using var db = NewDb();
        var storage = new FakeArtifactStorage();
        var sut = new CloneProjectUseCase(db, storage, new NullAuditLogger());

        await sut.ExecuteAsync(new CloneProjectVm { ProjectId = _projectId, Scope = ProjectCloneScope.Full }, "carol");

        Assert.Null(Assert.Single(storage.Copies).Folders);
        Assert.Empty(storage.Initialized);
    }

    [Fact]
    public async Task Clone_WithUnknownProject_ReturnsNotFound()
    {
        await using var db = NewDb();
        var sut = new CloneProjectUseCase(db, new FakeArtifactStorage(), new NullAuditLogger());

        var (result, _) = await sut.ExecuteAsync(new CloneProjectVm { ProjectId = Guid.NewGuid() }, "carol");

        Assert.Equal(CloneProjectResult.ProjectNotFound, result);
    }

    [Fact]
    public async Task Clone_WithoutName_DerivesTheDefault_AndStaysInsideTheColumnLimit()
    {
        // Đây là đường mà [MaxLength(200)] trên VM KHÔNG chặn được: Name rỗng nên ModelState hợp lệ, còn
        // tên dẫn xuất "{tên gốc} (bản sao)" thì vượt trần cột và làm SaveChanges ném ở SqlServer.
        var longNameId = Guid.NewGuid();
        await using (var seed = NewDb())
        {
            seed.Projects.Add(new Project { Id = longNameId, Name = new string('x', 198) });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        var sut = new CloneProjectUseCase(db, new FakeArtifactStorage(), new NullAuditLogger());

        var (result, newId) = await sut.ExecuteAsync(new CloneProjectVm { ProjectId = longNameId }, "carol");

        Assert.Equal(CloneProjectResult.Cloned, result);
        Assert.Equal(200, (await NewDb().Projects.SingleAsync(p => p.Id == newId)).Name.Length);
    }

    [Fact]
    public async Task Clone_WithExplicitName_TrimsIt()
    {
        await using var db = NewDb();
        var sut = new CloneProjectUseCase(db, new FakeArtifactStorage(), new NullAuditLogger());

        var (_, newId) = await sut.ExecuteAsync(
            new CloneProjectVm { ProjectId = _projectId, Name = "  Nghỉ phép - bản thử  " }, "carol");

        Assert.Equal("Nghỉ phép - bản thử", (await NewDb().Projects.SingleAsync(p => p.Id == newId)).Name);
    }

    private async Task<Guid> CloneAsync(ProjectCloneScope scope)
    {
        await using var db = NewDb();
        var sut = new CloneProjectUseCase(db, new FakeArtifactStorage(), new NullAuditLogger());

        var (result, newId) = await sut.ExecuteAsync(new CloneProjectVm { ProjectId = _projectId, Scope = scope }, "carol");

        Assert.Equal(CloneProjectResult.Cloned, result);
        return newId!.Value;
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeArtifactStorage : IArtifactStorage
    {
        public bool CopySucceeds { get; init; } = true;
        public List<(string SourceKey, string TargetKey, IReadOnlyCollection<string>? Folders)> Copies { get; } = new();
        public List<string> Initialized { get; } = new();
        public List<string> Deleted { get; } = new();

        public void InitializeProjectWorkspace(string projectKey) => Initialized.Add(projectKey);
        public bool TryRenameProjectWorkspace(string oldProjectKey, string newProjectKey) => true;

        public bool TryCopyProjectWorkspace(string sourceProjectKey, string targetProjectKey, IReadOnlyCollection<string>? onlyTopLevelFolders = null)
        {
            Copies.Add((sourceProjectKey, targetProjectKey, onlyTopLevelFolders));
            return CopySucceeds;
        }

        public void TryDeleteProjectWorkspace(string projectKey) => Deleted.Add(projectKey);

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
    }
}
