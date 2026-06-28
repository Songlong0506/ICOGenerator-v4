using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Reject cancels a delivery run that is waiting for human approval — EXCEPT at the POC gate, where a wrong
// POC means the requirement needs changing, which is the end-user's job (chat BA → Approve a new version),
// not TeamDev's on the Agent Dashboard. The use case guards that server-side regardless of the UI.
public class RejectStageUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public RejectStageUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ExecuteAsync_CancelsRun_AtNonPocGate()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.WorkflowRuns.Add(new WorkflowRun
            {
                Id = runId,
                ProjectId = projectId,
                Status = WorkflowRunStatus.WaitingForHuman,
                CurrentStage = WorkflowStageKey.ArchitectureDesign
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var result = await new RejectStageUseCase(db).ExecuteAsync(projectId, runId);
            Assert.Equal(RejectStageResult.Rejected, result);
        }

        await using (var db = NewDb())
        {
            var run = await db.WorkflowRuns.FirstAsync(x => x.Id == runId);
            Assert.Equal(WorkflowRunStatus.Canceled, run.Status);
            Assert.NotNull(run.FinishedAt);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RefusesAndKeepsRun_AtPocGate()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.WorkflowRuns.Add(new WorkflowRun
            {
                Id = runId,
                ProjectId = projectId,
                Status = WorkflowRunStatus.WaitingForHuman,
                CurrentStage = WorkflowStageKey.PocPreview
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var result = await new RejectStageUseCase(db).ExecuteAsync(projectId, runId);
            Assert.Equal(RejectStageResult.PocGateNotRejectable, result);
        }

        await using (var db = NewDb())
        {
            // Run stays untouched — changing the requirement is the user's path, not a reject here.
            var run = await db.WorkflowRuns.FirstAsync(x => x.Id == runId);
            Assert.Equal(WorkflowRunStatus.WaitingForHuman, run.Status);
            Assert.Null(run.FinishedAt);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNoWaitingRun_WhenNothingWaiting()
    {
        var projectId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.WorkflowRuns.Add(new WorkflowRun
            {
                ProjectId = projectId,
                Status = WorkflowRunStatus.Running,
                CurrentStage = WorkflowStageKey.ArchitectureDesign
            });
            await db.SaveChangesAsync();
        }

        await using var readDb = NewDb();
        var result = await new RejectStageUseCase(readDb).ExecuteAsync(projectId);

        Assert.Equal(RejectStageResult.NoWaitingRun, result);
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
