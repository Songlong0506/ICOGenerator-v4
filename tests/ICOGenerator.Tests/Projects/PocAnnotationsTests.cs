using ICOGenerator.Application.Projects;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Notifications;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Annotation trên POC: người xem click phần tử + ghi nhận xét (Open) → "Gửi đội Dev" (Submitted, kèm
// thông báo) → đội Dev gom mọi góp ý chưa xử lý thành MỘT yêu cầu chỉnh sửa POC qua đúng cơ chế
// RequestStageRevision (Processed). Chiều hỏng an toàn: không enqueue được thì annotation giữ trạng
// thái cũ — không bao giờ mất góp ý.
public class PocAnnotationsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PocAnnotationsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task AddAnnotation_DefaultsGeneralLabel_AndRejectsBlankComment()
    {
        var projectId = await SeedProjectAsync();

        await using (var db = NewDb())
        {
            var useCase = new AddPocAnnotationUseCase(db);

            Assert.Equal(AddPocAnnotationResult.MissingComment,
                await useCase.ExecuteAsync(projectId, "Nút \"Lưu\"", null, "   ", "bob"));
            Assert.Equal(AddPocAnnotationResult.ProjectNotFound,
                await useCase.ExecuteAsync(Guid.NewGuid(), null, null, "ok", "bob"));

            Assert.Equal(AddPocAnnotationResult.Added,
                await useCase.ExecuteAsync(projectId, "  ", "form > button", "Nút này phải màu đỏ.", "bob"));
        }

        await using (var db = NewDb())
        {
            var annotation = await db.PocAnnotations.SingleAsync();
            Assert.Equal(AddPocAnnotationUseCase.GeneralLabel, annotation.ElementLabel);
            Assert.Equal("form > button", annotation.ElementPath);
            Assert.Equal(PocAnnotationStatus.Open, annotation.Status);
            Assert.Equal("bob", annotation.AuthorUsername);
        }
    }

    [Fact]
    public async Task DeleteAnnotation_OnlyAuthor_AndOnlyWhileOpen()
    {
        var projectId = await SeedProjectAsync();
        var openId = await SeedAnnotationAsync(projectId, "bob", PocAnnotationStatus.Open);
        var submittedId = await SeedAnnotationAsync(projectId, "bob", PocAnnotationStatus.Submitted);

        await using var db = NewDb();
        var useCase = new DeletePocAnnotationUseCase(db);

        Assert.Equal(DeletePocAnnotationResult.NotAllowed, await useCase.ExecuteAsync(openId, "carol"));
        Assert.Equal(DeletePocAnnotationResult.NotAllowed, await useCase.ExecuteAsync(submittedId, "bob"));
        Assert.Equal(DeletePocAnnotationResult.Deleted, await useCase.ExecuteAsync(openId, "bob"));
        Assert.Equal(DeletePocAnnotationResult.NotFound, await useCase.ExecuteAsync(openId, "bob"));
    }

    [Fact]
    public async Task SubmitAnnotations_MarksOpenAsSubmitted_AndNotifiesDevTeam()
    {
        var projectId = await SeedProjectAsync();
        await SeedAnnotationAsync(projectId, "bob", PocAnnotationStatus.Open);
        await SeedAnnotationAsync(projectId, "carol", PocAnnotationStatus.Open);
        await SeedAnnotationAsync(projectId, "bob", PocAnnotationStatus.Processed);

        var notifications = new RecordingNotificationService();

        await using (var db = NewDb())
        {
            var result = await new SubmitPocAnnotationsUseCase(db, notifications).ExecuteAsync(projectId, "bob");
            Assert.Equal(SubmitPocAnnotationsResult.Submitted, result);
        }

        // Gửi CẢ LÔ các góp ý mở (kể cả của người khác) — một vòng review là một gói phản hồi.
        var call = Assert.Single(notifications.PocFeedbackCalls);
        Assert.Equal((projectId, "bob", 2), call);

        await using (var db = NewDb())
        {
            Assert.Equal(2, await db.PocAnnotations.CountAsync(a => a.Status == PocAnnotationStatus.Submitted));
            Assert.All(
                await db.PocAnnotations.Where(a => a.Status == PocAnnotationStatus.Submitted).ToListAsync(),
                a => Assert.NotNull(a.SubmittedAt));

            // Lần hai: không còn gì mở → NothingToSubmit, không thông báo thêm.
            var again = await new SubmitPocAnnotationsUseCase(db, notifications).ExecuteAsync(projectId, "bob");
            Assert.Equal(SubmitPocAnnotationsResult.NothingToSubmit, again);
            Assert.Single(notifications.PocFeedbackCalls);
        }
    }

    [Fact]
    public async Task ApplyAnnotations_QueuesPocRevision_AndMarksProcessed()
    {
        var projectId = await SeedProjectAsync();
        var runId = await SeedWaitingPocRunAsync(projectId);
        await SeedAnnotationAsync(projectId, "bob", PocAnnotationStatus.Open, label: "Nút \"Lưu\"", comment: "Đổi sang màu đỏ.");
        await SeedAnnotationAsync(projectId, "carol", PocAnnotationStatus.Submitted, label: "Bảng danh sách", comment: "Thiếu cột ngày tạo.");

        await using (var db = NewDb())
        {
            var result = await new ApplyPocAnnotationsRevisionUseCase(db, new RequestStageRevisionUseCase(db))
                .ExecuteAsync(projectId);
            Assert.Equal(ApplyPocAnnotationsRevisionResult.Queued, result);
        }

        await using (var db = NewDb())
        {
            // Một task chỉnh sửa POC được enqueue, feedback chứa ĐỦ các góp ý theo thứ tự tạo.
            var revision = await db.AgentTasks.SingleAsync(t => t.RevisionFeedback != null);
            Assert.Equal(AgentTaskType.PocPreview, revision.Type);
            Assert.Equal(AgentTaskStatus.Queued, revision.Status);
            Assert.Contains("[Nút \"Lưu\"] — Đổi sang màu đỏ. (góp ý bởi bob)", revision.RevisionFeedback);
            Assert.Contains("[Bảng danh sách] — Thiếu cột ngày tạo. (góp ý bởi carol)", revision.RevisionFeedback);

            var run = await db.WorkflowRuns.SingleAsync(r => r.Id == runId);
            Assert.Equal(WorkflowRunStatus.Queued, run.Status);

            // Mọi annotation đã gom đều chuyển Processed.
            Assert.Equal(2, await db.PocAnnotations.CountAsync(a => a.Status == PocAnnotationStatus.Processed));
        }
    }

    [Fact]
    public async Task ApplyAnnotations_WithoutWaitingRun_KeepsAnnotationsUntouched()
    {
        var projectId = await SeedProjectAsync();
        await SeedAnnotationAsync(projectId, "bob", PocAnnotationStatus.Submitted);

        await using (var db = NewDb())
        {
            var result = await new ApplyPocAnnotationsRevisionUseCase(db, new RequestStageRevisionUseCase(db))
                .ExecuteAsync(projectId);
            Assert.Equal(ApplyPocAnnotationsRevisionResult.NoWaitingRun, result);
        }

        await using (var db = NewDb())
        {
            // Không enqueue được thì góp ý giữ nguyên trạng thái — lần bấm sau gom lại, không mất gì.
            var annotation = await db.PocAnnotations.SingleAsync();
            Assert.Equal(PocAnnotationStatus.Submitted, annotation.Status);
            Assert.Null(annotation.ProcessedAt);
        }
    }

    [Fact]
    public async Task ApplyAnnotations_WithNothingPending_ReturnsNothingToApply()
    {
        var projectId = await SeedProjectAsync();
        await SeedWaitingPocRunAsync(projectId);
        await SeedAnnotationAsync(projectId, "bob", PocAnnotationStatus.Processed);

        await using var db = NewDb();
        var result = await new ApplyPocAnnotationsRevisionUseCase(db, new RequestStageRevisionUseCase(db))
            .ExecuteAsync(projectId);

        Assert.Equal(ApplyPocAnnotationsRevisionResult.NothingToApply, result);
    }

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = NewDb();
        var project = new Project { Name = "P", CreatedByUsername = "alice" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task<Guid> SeedAnnotationAsync(
        Guid projectId, string author, PocAnnotationStatus status,
        string label = "Phần tử", string comment = "nhận xét")
    {
        await using var db = NewDb();
        var annotation = new PocAnnotation
        {
            ProjectId = projectId,
            AuthorUsername = author,
            ElementLabel = label,
            Comment = comment,
            Status = status,
            SubmittedAt = status != PocAnnotationStatus.Open ? DateTime.UtcNow : null,
            ProcessedAt = status == PocAnnotationStatus.Processed ? DateTime.UtcNow : null
        };
        db.PocAnnotations.Add(annotation);
        await db.SaveChangesAsync();
        return annotation.Id;
    }

    // Run đang dừng ở cổng POC + task POC đã hoàn tất — điều kiện để RequestStageRevision enqueue được.
    private async Task<Guid> SeedWaitingPocRunAsync(Guid projectId)
    {
        await using var db = NewDb();
        var run = new WorkflowRun
        {
            ProjectId = projectId,
            Status = WorkflowRunStatus.WaitingForHuman,
            CurrentStage = WorkflowStageKey.PocPreview
        };
        db.WorkflowRuns.Add(run);
        db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = run.Id,
            ProjectId = projectId,
            Type = AgentTaskType.PocPreview,
            Status = AgentTaskStatus.Completed,
            Title = "Tạo POC HTML để xem trước",
            Input = "spec",
            FinishedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return run.Id;
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }

    // Fake ghi lại lời gọi thông báo — đủ để khẳng định use case bắn đúng sự kiện, không cần dựng
    // NotificationService thật (permission service + channels).
    private sealed class RecordingNotificationService : INotificationService
    {
        public List<(Guid ProjectId, string? Actor, int Count)> PocFeedbackCalls { get; } = new();

        public Task NotifyGateOpenedAsync(WorkflowRun run, string nextStepTitle, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyRunCompletedAsync(WorkflowRun run, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyRunFailedAsync(WorkflowRun run, string? error, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyPocFeedbackSubmittedAsync(Guid projectId, string? submittedByUsername, int annotationCount, CancellationToken cancellationToken = default)
        {
            PocFeedbackCalls.Add((projectId, submittedByUsername, annotationCount));
            return Task.CompletedTask;
        }
    }
}
