using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
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

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // The ApiKey value-converter needs an IApiKeyProtector; encryption is irrelevant to these tests.
    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
