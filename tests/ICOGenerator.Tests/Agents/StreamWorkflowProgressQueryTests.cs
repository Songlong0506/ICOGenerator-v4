using ICOGenerator.Application.Agents;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Agents;

// End-to-end check of the SSE streaming query (backlog → live token → live milestone → terminal close)
// against a real AppDbContext (Sqlite in-memory) and a real WorkflowProgressReporter.
public class StreamWorkflowProgressQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public StreamWorkflowProgressQueryTests()
    {
        // A single shared open connection keeps the in-memory database alive for the test's lifetime.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ExecuteAsync_StreamsBacklogThenLive_AndClosesWhenTerminal()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.WorkflowRuns.Add(new WorkflowRun { Id = runId, ProjectId = projectId, Status = WorkflowRunStatus.Running });
            await db.SaveChangesAsync();
        }

        var reporter = new WorkflowProgressReporter();
        // Backlog: emitted before anyone connects; must be replayed first.
        reporter.Report(runId, "start", "Bắt đầu task");

        await using var readDb = NewDb();
        var query = new StreamWorkflowProgressQuery(readDb, reporter);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var e = query.ExecuteAsync(projectId, runId, 0, cts.Token).GetAsyncEnumerator(cts.Token);

        // 1) Backlog replay.
        Assert.True(await e.MoveNextAsync());
        Assert.Equal("start", e.Current!.Kind);

        // 2) Live token (Seq 0) streamed through.
        reporter.ReportToken(runId, "Hello");
        Assert.True(await e.MoveNextAsync());
        Assert.Equal("token", e.Current!.Kind);
        Assert.Equal("Hello", e.Current.Message);

        // 3) Live milestone.
        reporter.Report(runId, "tool", "Đang dùng tool");
        Assert.True(await e.MoveNextAsync());
        Assert.Equal("tool", e.Current!.Kind);

        // 4) Run becomes terminal, then a "completed" milestone arrives: it is yielded, then the stream closes.
        await using (var db = NewDb())
        {
            var run = await db.WorkflowRuns.FirstAsync(x => x.Id == runId);
            run.Status = WorkflowRunStatus.Completed;
            await db.SaveChangesAsync();
        }
        reporter.Report(runId, "completed", "Xong");

        Assert.True(await e.MoveNextAsync());
        Assert.Equal("completed", e.Current!.Kind);

        // Terminal → enumerator completes (SSE connection would close here).
        Assert.False(await e.MoveNextAsync());
    }

    [Fact]
    public async Task ExecuteAsync_YieldsNothing_WhenRunDoesNotBelongToProject()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var otherProjectId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.Projects.Add(new Project { Id = otherProjectId, Name = "Other" });
            // Run belongs to a DIFFERENT project than the one queried.
            db.WorkflowRuns.Add(new WorkflowRun { Id = runId, ProjectId = otherProjectId, Status = WorkflowRunStatus.Running });
            await db.SaveChangesAsync();
        }

        await using var readDb = NewDb();
        var query = new StreamWorkflowProgressQuery(readDb, new WorkflowProgressReporter());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = query.ExecuteAsync(projectId, runId, 0, cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.False(await e.MoveNextAsync());
    }

    [Fact]
    public async Task ExecuteAsync_ReplaysBacklogThenCloses_WhenAlreadyTerminalAtConnect()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.WorkflowRuns.Add(new WorkflowRun { Id = runId, ProjectId = projectId, Status = WorkflowRunStatus.Completed });
            await db.SaveChangesAsync();
        }

        var reporter = new WorkflowProgressReporter();
        reporter.Report(runId, "completed", "Đã xong từ trước");

        await using var readDb = NewDb();
        var query = new StreamWorkflowProgressQuery(readDb, reporter);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = query.ExecuteAsync(projectId, runId, 0, cts.Token).GetAsyncEnumerator(cts.Token);

        // Backlog is replayed for a finished run, then the stream closes immediately (no live wait).
        Assert.True(await e.MoveNextAsync());
        Assert.Equal("completed", e.Current!.Kind);
        Assert.False(await e.MoveNextAsync());
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
