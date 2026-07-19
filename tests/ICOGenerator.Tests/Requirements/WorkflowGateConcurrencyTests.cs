using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// WorkflowRun.Status / AgentTask.Status là CONCURRENCY TOKEN: hai request song song cùng thao tác một
// cổng WaitingForHuman (double-click, hai người duyệt) thì chỉ MỘT bên thắng — bên thua không được
// enqueue task trùng (đốt token gấp đôi) hay ghi đè trạng thái mới bằng trạng thái cũ.
public class WorkflowGateConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public WorkflowGateConcurrencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task RunStatus_IsConcurrencyToken_StaleUpdateThrows()
    {
        // Chứng minh token hoạt động trên chính provider test (Sqlite): hai context cùng nạp một run,
        // bên lưu sau phải nhận DbUpdateConcurrencyException thay vì last-write-wins.
        var (projectId, runId) = await SeedWaitingRunAsync(WorkflowStageKey.ArchitectureDesign);

        await using var db1 = NewDb();
        await using var db2 = NewDb();
        var run1 = await db1.WorkflowRuns.FirstAsync(x => x.Id == runId);
        var run2 = await db2.WorkflowRuns.FirstAsync(x => x.Id == runId);

        run1.Status = WorkflowRunStatus.Queued;
        await db1.SaveChangesAsync();

        run2.Status = WorkflowRunStatus.Canceled;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task ApproveStage_LosingRace_ReturnsNoPendingStage_AndDoesNotEnqueueDuplicateTask()
    {
        // Mô phỏng đúng thứ tự của một race: use case ĐÃ đọc run WaitingForHuman, rồi một request song
        // song advance run TRƯỚC KHI use case kịp lưu — interceptor chen "request kia" vào ngay trước
        // SaveChanges đầu tiên của use case.
        var (projectId, runId) = await SeedWaitingRunAsync(WorkflowStageKey.PocPreview);

        var interceptor = new BeforeSaveInterceptor(async () =>
        {
            await using var rival = NewDb();
            var run = await rival.WorkflowRuns.FirstAsync(x => x.Id == runId);
            run.Status = WorkflowRunStatus.Queued; // "người kia" vừa Duyệt xong
            await rival.SaveChangesAsync();
        });

        await using (var db = NewDb(interceptor))
        {
            var result = await new ApproveStageUseCase(db, new ProjectArtifactCatalog()).ExecuteAsync(projectId, runId);
            Assert.Equal(ApproveStageResult.NoPendingStage, result);
        }

        await using (var check = NewDb())
        {
            // Bên thua không được để lại task bước kế nào (seed có sẵn một task POC Completed) —
            // task TechnicalDocs trùng là chính lỗi double-approve cũ.
            Assert.Equal(0, await check.AgentTasks.CountAsync(
                t => t.WorkflowRunId == runId && t.Type == AgentTaskType.TechnicalDocs));
            // Trạng thái của bên thắng được giữ nguyên.
            Assert.Equal(WorkflowRunStatus.Queued, (await check.WorkflowRuns.FirstAsync(x => x.Id == runId)).Status);
        }
    }

    [Fact]
    public async Task ApproveStage_SequentialSecondApprove_SeesNoPendingStage()
    {
        // Double-click "chậm" (request thứ hai tới sau khi request đầu đã lưu): nhánh SELECT đã lọc
        // WaitingForHuman nên không cần tới token — vẫn phải trả NoPendingStage và chỉ MỘT task được tạo.
        var (projectId, runId) = await SeedWaitingRunAsync(WorkflowStageKey.PocPreview);

        await using (var db = NewDb())
            Assert.Equal(ApproveStageResult.Advanced, await new ApproveStageUseCase(db, new ProjectArtifactCatalog()).ExecuteAsync(projectId, runId));

        await using (var db = NewDb())
            Assert.Equal(ApproveStageResult.NoPendingStage, await new ApproveStageUseCase(db, new ProjectArtifactCatalog()).ExecuteAsync(projectId, runId));

        await using (var check = NewDb())
            Assert.Equal(1, await check.AgentTasks.CountAsync(
                t => t.WorkflowRunId == runId && t.Type == AgentTaskType.TechnicalDocs));
    }

    [Fact]
    public async Task RejectStage_LosingRace_ReturnsNoWaitingRun_AndKeepsWinnerStatus()
    {
        var (projectId, runId) = await SeedWaitingRunAsync(WorkflowStageKey.ArchitectureDesign);

        var interceptor = new BeforeSaveInterceptor(async () =>
        {
            await using var rival = NewDb();
            var run = await rival.WorkflowRuns.FirstAsync(x => x.Id == runId);
            run.Status = WorkflowRunStatus.Queued; // người kia vừa Duyệt — run không còn chờ ở cổng
            await rival.SaveChangesAsync();
        });

        await using (var db = NewDb(interceptor))
        {
            var result = await new RejectStageUseCase(db).ExecuteAsync(projectId, runId);
            Assert.Equal(RejectStageResult.NoWaitingRun, result);
        }

        await using (var check = NewDb())
            // Không được ghi đè Queued (của người thắng) bằng Canceled.
            Assert.Equal(WorkflowRunStatus.Queued, (await check.WorkflowRuns.FirstAsync(x => x.Id == runId)).Status);
    }

    private async Task<(Guid ProjectId, Guid RunId)> SeedWaitingRunAsync(WorkflowStageKey stage)
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using var db = NewDb();

        // ApproveStage ở cổng POC enqueue bước kế (TechnicalDocs — vai BA) nên cần sẵn agent BA + model.
        if (!await db.Agents.AnyAsync(a => a.RoleKey == AgentRoleKey.BusinessAnalyst))
        {
            var model = new AiModel { ModelId = "m", Endpoint = "http://localhost", ApiKey = "" };
            db.AiModels.Add(model);
            db.Agents.Add(new Agent { RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });
        }

        db.Projects.Add(new Project { Id = projectId, Name = "P" });
        db.WorkflowRuns.Add(new WorkflowRun
        {
            Id = runId,
            ProjectId = projectId,
            Status = WorkflowRunStatus.WaitingForHuman,
            CurrentStage = stage
        });
        // Task Completed của bước hiện tại — trạng thái bình thường của một run đang chờ duyệt.
        db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = runId,
            ProjectId = projectId,
            Type = AgentTaskType.PocPreview,
            Status = AgentTaskStatus.Completed,
            Title = "POC",
            FinishedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return (projectId, runId);
    }

    private AppDbContext NewDb(BeforeSaveInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection);
        if (interceptor != null)
            builder.AddInterceptors(interceptor);
        return new AppDbContext(interceptor == null ? _options : builder.Options, new PassthroughApiKeyProtector());
    }

    public void Dispose() => _connection.Dispose();

    // Chạy một callback ĐÚNG MỘT LẦN ngay trước SaveChanges đầu tiên của context được gắn — chen một
    // "request song song" vào giữa lúc use case đã đọc và sắp lưu, để kích hoạt đường concurrency token
    // một cách tất định (không cần Task song song thật + may rủi timing).
    private sealed class BeforeSaveInterceptor : SaveChangesInterceptor
    {
        private Func<Task>? _onSaving;

        public BeforeSaveInterceptor(Func<Task> onSaving) => _onSaving = onSaving;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var callback = _onSaving;
            _onSaving = null;
            if (callback != null)
                await callback();
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
