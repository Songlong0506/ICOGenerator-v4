using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// A delivery step can fail on a transient LLM/network error. Retry must re-queue the SAME failed task
// (not start a fresh workflow) and restore the run's real stage so the worker resumes from where it broke.
public class RetryWorkflowUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public RetryWorkflowUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ExecuteAsync_RequeuesFailedTask_AndRestoresStage()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.WorkflowRuns.Add(new WorkflowRun
            {
                Id = runId,
                ProjectId = projectId,
                Status = WorkflowRunStatus.Failed,
                CurrentStage = WorkflowStageKey.Failed,
                FinishedAt = DateTime.UtcNow
            });
            db.AgentTasks.Add(new AgentTask
            {
                Id = taskId,
                WorkflowRunId = runId,
                ProjectId = projectId,
                Type = AgentTaskType.PocPreview,
                Status = AgentTaskStatus.Failed,
                Title = "Tạo POC HTML để xem trước",
                Input = "design spec",
                Error = "LLM call failed",
                FinishedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var result = await new RetryWorkflowUseCase(db).ExecuteAsync(projectId, runId);
            Assert.Equal(RetryWorkflowResult.Requeued, result);
        }

        await using (var db = NewDb())
        {
            var run = await db.WorkflowRuns.FirstAsync(x => x.Id == runId);
            Assert.Equal(WorkflowRunStatus.Queued, run.Status);
            Assert.Equal(WorkflowStageKey.PocPreview, run.CurrentStage); // stage restored from the failed task
            Assert.Null(run.FinishedAt);

            var task = await db.AgentTasks.FirstAsync(x => x.Id == taskId);
            Assert.Equal(AgentTaskStatus.Queued, task.Status);
            Assert.Null(task.Error);
            Assert.Null(task.FinishedAt);
            Assert.Equal("design spec", task.Input); // input preserved → no re-generation needed
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNoFailedRun_WhenRunNotFailed()
    {
        var projectId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.WorkflowRuns.Add(new WorkflowRun
            {
                ProjectId = projectId,
                Status = WorkflowRunStatus.WaitingForHuman,
                CurrentStage = WorkflowStageKey.PocPreview
            });
            await db.SaveChangesAsync();
        }

        await using var readDb = NewDb();
        var result = await new RetryWorkflowUseCase(readDb).ExecuteAsync(projectId);

        Assert.Equal(RetryWorkflowResult.NoFailedRun, result);
    }

    // "Thử lại" mặc định là CHẠY TIẾP: checkpoint của lượt agent (các bước đã xong trước khi lỗi) phải còn
    // nguyên để worker nối vào. Chỉ khi người dùng chọn chạy lại từ đầu thì nó mới bị bỏ — và phải bị bỏ
    // trước khi task về Queued, nếu không worker kịp nhặt task và nối vào đúng hội thoại vừa bị bảo bỏ.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task ExecuteAsync_KeepsTheCheckpoint_UnlessRestartIsRequested(bool restart, int expectedCheckpoints)
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.WorkflowRuns.Add(new WorkflowRun
            {
                Id = runId, ProjectId = projectId,
                Status = WorkflowRunStatus.Failed, CurrentStage = WorkflowStageKey.Failed, FinishedAt = DateTime.UtcNow
            });
            db.AgentTasks.Add(new AgentTask
            {
                Id = taskId, WorkflowRunId = runId, ProjectId = projectId,
                Type = AgentTaskType.PocPreview, Status = AgentTaskStatus.Failed,
                Input = "design spec", Error = "LLM call failed", FinishedAt = DateTime.UtcNow
            });
            db.AgentRunCheckpoints.Add(new AgentRunCheckpoint { TaskId = taskId, MessagesJson = "[]", CompletedSteps = 9 });
            await db.SaveChangesAsync();
        }

        // Trước khi bấm: màn hình trạng thái báo đúng số bước sẽ được giữ.
        await using (var db = NewDb())
        {
            var status = await new GetWorkflowStatusQuery(db, new WorkflowProgressReporter()).ExecuteAsync(projectId, runId);
            Assert.Equal(9, status.ResumableSteps);
        }

        await using (var db = NewDb())
            Assert.Equal(RetryWorkflowResult.Requeued, await new RetryWorkflowUseCase(db).ExecuteAsync(projectId, runId, restart));

        await using (var db = NewDb())
        {
            Assert.Equal(expectedCheckpoints, await db.AgentRunCheckpoints.CountAsync(x => x.TaskId == taskId));
            Assert.Equal(AgentTaskStatus.Queued, (await db.AgentTasks.FirstAsync(x => x.Id == taskId)).Status);

            // Run không còn Failed ⇒ không còn nút thử lại nào để hứa giữ bước.
            var status = await new GetWorkflowStatusQuery(db, new WorkflowProgressReporter()).ExecuteAsync(projectId, runId);
            Assert.Equal(0, status.ResumableSteps);
        }
    }

    // Xoá task (xoá run/dự án) kéo theo checkpoint — không để lại hội thoại mồ côi chứa nguyên văn spec.
    [Fact]
    public async Task DeletingTheTask_DeletesItsCheckpoint()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.WorkflowRuns.Add(new WorkflowRun { Id = runId, ProjectId = projectId });
            db.AgentTasks.Add(new AgentTask { Id = taskId, WorkflowRunId = runId, ProjectId = projectId });
            db.AgentRunCheckpoints.Add(new AgentRunCheckpoint { TaskId = taskId, MessagesJson = "[]", CompletedSteps = 1 });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            db.WorkflowRuns.Remove(await db.WorkflowRuns.FirstAsync(x => x.Id == runId));
            await db.SaveChangesAsync();
        }

        Assert.Empty(await NewDb().AgentRunCheckpoints.ToListAsync());
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();
}
