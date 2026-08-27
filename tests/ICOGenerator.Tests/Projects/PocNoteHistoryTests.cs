using ICOGenerator.Application.Projects;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// LỊCH SỬ GHI CHÚ theo phiên bản Product Brief: ghi chú Brief + ghi chú POC + các vòng Dev chỉnh demo
// nằm chung một bảng, gom theo version, không dòng nào bị xoá. Ba chốt được kiểm ở đây:
//   1. Ghi chú Brief đóng dấu "draft" rồi được Approve NÂNG lên V{n} cùng lúc với file — không thì
//      ghi chú của bản vừa duyệt bị gán cho một phiên bản không tồn tại.
//   2. Ghi chú của mọi version đều còn (đây chính là điều người dùng phàn nàn: approve xong là "mất hết").
//   3. Bàn giao của vòng sửa về đúng dòng của nó, và dòng thu hồi vẫn hiện với dấu thu hồi.
public class PocNoteHistoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public PocNoteHistoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Approve_PromotesDraftBriefNotes_ToTheApprovedVersion()
    {
        await using (var db = NewDb())
        {
            db.ProjectDocuments.Add(new ProjectDocument
            {
                ProjectId = _projectId,
                VersionName = "draft",
                IsApproved = false,
                Folder = "01_Requirement",
                FileName = "ProductBrief.docx",
                Content = "bản nháp"
            });
            db.PocComments.AddRange(
                new PocComment
                {
                    ProjectId = _projectId, Target = PocCommentTarget.Brief, Comment = "thiếu mục phân quyền",
                    Quote = "Người dùng đăng nhập", CreatedByUsername = "user"
                },
                // Ghi chú POC đã đóng dấu V-cụ-thể KHÔNG bị nâng theo: nó nói về bản demo của bản cũ.
                new PocComment
                {
                    ProjectId = _projectId, Comment = "nút Lưu sai nhãn", BriefVersion = "V1", CreatedByUsername = "user"
                });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
            Assert.Equal(ApproveRequirementResult.Approved, await NewApproveUseCase(db).ExecuteAsync(_projectId));

        await using (var db = NewDb())
        {
            var notes = await db.PocComments.ToListAsync();
            Assert.Equal("V1", notes.Single(n => n.Target == PocCommentTarget.Brief).BriefVersion);
            Assert.Equal("V1", notes.Single(n => n.Target == PocCommentTarget.Poc).BriefVersion);
        }
    }

    [Fact]
    public async Task History_GroupsEveryVersion_NewestFirst_AndKeepsWithdrawnRows()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.WorkflowRuns.Add(new WorkflowRun { Id = runId, ProjectId = _projectId });
            db.AgentTasks.Add(new AgentTask
            {
                Id = taskId,
                WorkflowRunId = runId,
                ProjectId = _projectId,
                Type = AgentTaskType.PocPreview,
                Status = AgentTaskStatus.Completed,
                Title = "Tạo POC HTML để xem trước (chỉnh sửa lần 1)",
                RevisionFeedback = "sửa nhãn nút",
                Output = "Đã đổi nhãn nút Save thành Lưu.",
                FinishedAt = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc)
            });

            db.PocComments.AddRange(
                new PocComment
                {
                    ProjectId = _projectId, Target = PocCommentTarget.Brief, BriefVersion = "V1",
                    Quote = "đoạn bị chê", Comment = "thiếu mục phân quyền", CreatedByUsername = "user",
                    Status = PocCommentStatus.RoutedToRequirement, Route = PocCommentRoute.Requirement,
                    CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new PocComment
                {
                    ProjectId = _projectId, BriefVersion = "V1", PageView = "JD Library", ElementLabel = "Nút Lưu",
                    Comment = "sai nhãn", CreatedByUsername = "user",
                    Status = PocCommentStatus.Addressed, Route = PocCommentRoute.FixPoc,
                    RevisionTaskId = taskId, AddressedNote = "Đã đổi nhãn nút Save thành Lưu.",
                    AddressedAtUtc = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc),
                    CreatedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc)
                },
                new PocComment
                {
                    ProjectId = _projectId, BriefVersion = "V2", Comment = "gõ nhầm", CreatedByUsername = "user",
                    WithdrawnAtUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), WithdrawnByUsername = "user",
                    CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)
                });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var history = await new GetPocNoteHistoryQuery(db).ExecuteAsync(_projectId);

            // Bản mới nhất lên đầu.
            Assert.Equal(new[] { "V2", "V1" }, history.Select(v => v.BriefVersion).ToArray());

            var v2 = Assert.Single(history.Single(v => v.BriefVersion == "V2").Rows);
            Assert.True(v2.Withdrawn);
            Assert.Equal("user", v2.WithdrawnBy);

            // V1: ghi chú Brief, ghi chú POC và vòng sửa — theo thứ tự thời gian.
            var v1 = history.Single(v => v.BriefVersion == "V1").Rows;
            Assert.Equal(
                new[] { PocNoteHistoryKind.BriefNote, PocNoteHistoryKind.PocNote, PocNoteHistoryKind.Revision },
                v1.Select(r => r.Kind).ToArray());

            // Vòng sửa đứng ở version của chính các ghi chú nó mang đi, và mang bàn giao TOÀN VĂN.
            var revision = v1.Single(r => r.Kind == PocNoteHistoryKind.Revision);
            Assert.Equal("Đã đổi nhãn nút Save thành Lưu.", revision.RepairLog);

            // Ghi chú Brief giữ được đoạn trích làm neo.
            Assert.Contains("đoạn bị chê", v1[0].Anchor);
        }
    }

    private ApproveRequirementUseCase NewApproveUseCase(AppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = Path.Combine(Path.GetTempPath(), "ico-tests", Guid.NewGuid().ToString("N"))
            })
            .Build();

        return new ApproveRequirementUseCase(
            db,
            new WorkspacePathResolver(config),
            new ProjectArtifactCatalog(),
            new FakeOrchestrator(),
            NullLogger<ApproveRequirementUseCase>.Instance);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeOrchestrator : IWorkflowOrchestrator
    {
        public Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId, bool coalesceWithActiveRun = false) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string v, string s) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> StartAiDesignSpecWorkflowAsync(Guid projectId, string v) => Task.FromResult(Guid.NewGuid());
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
