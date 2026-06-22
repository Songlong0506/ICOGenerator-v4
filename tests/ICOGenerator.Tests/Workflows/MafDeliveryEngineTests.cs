using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Workflows;
using ICOGenerator.Services.Workflows.Maf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

// Drives the live MafDeliveryEngine against a real EF (Sqlite) database + EF checkpoint store, with a
// fake stage runner standing in for the LLM. Verifies the engine's DB-facing behaviour: the run halts at
// each gate (WaitingForHuman), Approve advances exactly one stage, and the run completes after the final
// gate (running the automatic Testing→BugFix→Testing loop without a gate).
public class MafDeliveryEngineTests : IDisposable
{
    private const string DesignSpecFileName = "ai-design-spec.md";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public MafDeliveryEngineTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddSingleton<IApiKeyProtector, PassthroughApiKeyProtector>();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<IWorkflowProgressReporter, WorkflowProgressReporter>();
        services.AddSingleton<IProjectArtifactCatalog, FakeArtifactCatalog>();
        services.AddSingleton<IPipelineStageRunner, ScriptedStageRunner>();
        services.AddSingleton<DeliveryWorkflowFactory>();
        services.AddSingleton<EfWorkflowCheckpointStore>();
        services.AddSingleton<MafDeliveryEngine>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task DriveAsync_HaltsAtEachGate_AdvancesOnApproval_AndCompletes()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.ProjectDocuments.Add(new ProjectDocument
            {
                ProjectId = projectId,
                FileName = DesignSpecFileName,
                IsApproved = true,
                Content = "the approved design spec"
            });
            db.WorkflowRuns.Add(new WorkflowRun
            {
                Id = runId,
                ProjectId = projectId,
                Status = WorkflowRunStatus.Queued,
                CurrentStage = WorkflowStageKey.PocPreview
            });
            await db.SaveChangesAsync();
        }

        var engine = _services.GetRequiredService<MafDeliveryEngine>();

        // Initial drive: POC runs, then the run halts at the first approval gate.
        await engine.DriveAsync(runId, default);
        Assert.Equal(WorkflowRunStatus.WaitingForHuman, await StatusOf(runId));

        // Approve each gate. The four linear hand-offs (POC→Arch→Impl→CodeReview→Testing) each halt for
        // approval; after the fourth, Testing runs the FAIL→BugFix→PASS loop with no gate and completes.
        var approvals = 0;
        while (await StatusOf(runId) != WorkflowRunStatus.Completed)
        {
            Assert.Equal(WorkflowRunStatus.WaitingForHuman, await StatusOf(runId));
            await ApproveAsync(runId);
            await engine.DriveAsync(runId, default);

            Assert.True(++approvals <= 5, "Run did not complete within the expected number of approvals.");
        }

        Assert.Equal(4, approvals);

        // The Testing↔BugFix loop ran: Testing twice (FAIL then PASS) and BugFix once.
        var runner = (ScriptedStageRunner)_services.GetRequiredService<IPipelineStageRunner>();
        Assert.Equal(2, runner.CountOf(WorkflowStageKey.Testing));
        Assert.Equal(1, runner.CountOf(WorkflowStageKey.BugFix));
    }

    private async Task ApproveAsync(Guid runId)
    {
        await using var db = NewDb();
        var run = await db.WorkflowRuns.FirstAsync(r => r.Id == runId);
        run.PendingApprovalJson = MafDeliveryEngine.ApprovedMarker;
        run.Status = WorkflowRunStatus.Queued;
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowRunStatus> StatusOf(Guid runId)
    {
        await using var db = NewDb();
        return await db.WorkflowRuns.Where(r => r.Id == runId).Select(r => r.Status).FirstAsync();
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    // Returns canned per-stage output; Testing fails once (forcing a BugFix round) then passes.
    private sealed class ScriptedStageRunner : IPipelineStageRunner
    {
        private readonly Dictionary<WorkflowStageKey, int> _counts = new();
        private readonly object _lock = new();

        public int CountOf(WorkflowStageKey stage)
        {
            lock (_lock)
                return _counts.TryGetValue(stage, out var n) ? n : 0;
        }

        public Task<string> RunStageAsync(Guid workflowRunId, Guid projectId, PipelineStep step, string input, CancellationToken cancellationToken)
        {
            int count;
            lock (_lock)
                count = _counts[step.Stage] = CountOf(step.Stage) + 1;

            var output = step.Stage switch
            {
                WorkflowStageKey.Testing => count == 1 ? "VERDICT: FAIL" : "VERDICT: PASS",
                _ => $"{step.Stage} output"
            };
            return Task.FromResult(output);
        }
    }

    private sealed class FakeArtifactCatalog : IProjectArtifactCatalog
    {
        public IReadOnlyList<ProjectArtifactDescriptor> RequirementDocuments { get; } = Array.Empty<ProjectArtifactDescriptor>();
        public ProjectArtifactDescriptor AiDesignSpec { get; } =
            new("AIDesignSpec", DesignSpecFileName, "AI Design Spec", true, "Design");
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
