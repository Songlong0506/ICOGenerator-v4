using ICOGenerator.Application.Quality;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Quality;

// Tổng hợp chất lượng giao hàng trên AppDbContext thật (Sqlite): đếm kết quả run, rước việc (revision +
// bugfix), chi phí theo run, và độ tin cậy model — với lọc theo năm.
public class GetDeliveryQualityQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public GetDeliveryQualityQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    private static DateTime Utc(int year, int month, int day, int hour = 10, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Aggregates_Outcomes_Rework_Cost_And_ModelReliability()
    {
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var runA = Guid.NewGuid(); // P1 Completed, 10'
        var runB = Guid.NewGuid(); // P1 Failed @ Implementation
        var runC = Guid.NewGuid(); // P1 WaitingForHuman (in progress)
        var runD = Guid.NewGuid(); // P2 Completed, 20'
        var runE = Guid.NewGuid(); // P1 Completed nhưng năm 2023 (bị loại)

        var modelId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.AiModels.Add(new AiModel
            {
                Id = modelId, ModelId = "m1", Endpoint = "http://x", ApiKey = "",
                InputPricePerMillionTokens = 1m, OutputPricePerMillionTokens = 2m
            });
            // AgentModelCallLog.AgentId là FK Restrict tới Agent — cần một agent thật để tham chiếu.
            db.Agents.Add(new Agent { Id = agentId, RoleKey = AgentRoleKey.Developer, AiModelId = modelId });
            db.Projects.AddRange(
                new Project { Id = p1, Name = "Alpha" },
                new Project { Id = p2, Name = "Beta" });

            db.WorkflowRuns.AddRange(
                new WorkflowRun { Id = runA, ProjectId = p1, Status = WorkflowRunStatus.Completed, CurrentStage = WorkflowStageKey.Completed, CreatedAt = Utc(2026, 3, 1), StartedAt = Utc(2026, 3, 1, 10, 0), FinishedAt = Utc(2026, 3, 1, 10, 10) },
                new WorkflowRun { Id = runB, ProjectId = p1, Status = WorkflowRunStatus.Failed, CurrentStage = WorkflowStageKey.Implementation, CreatedAt = Utc(2026, 3, 2) },
                new WorkflowRun { Id = runC, ProjectId = p1, Status = WorkflowRunStatus.WaitingForHuman, CurrentStage = WorkflowStageKey.CodeReview, CreatedAt = Utc(2026, 3, 3) },
                new WorkflowRun { Id = runD, ProjectId = p2, Status = WorkflowRunStatus.Completed, CurrentStage = WorkflowStageKey.Completed, CreatedAt = Utc(2026, 3, 4), StartedAt = Utc(2026, 3, 4, 10, 0), FinishedAt = Utc(2026, 3, 4, 10, 20) },
                new WorkflowRun { Id = runE, ProjectId = p1, Status = WorkflowRunStatus.Completed, CurrentStage = WorkflowStageKey.Completed, CreatedAt = Utc(2023, 5, 1) });

            // Tasks: runA có 1 revision + 2 bugfix; runD có 1 revision.
            db.AgentTasks.AddRange(
                new AgentTask { WorkflowRunId = runA, ProjectId = p1, Type = AgentTaskType.Implementation, Status = AgentTaskStatus.Completed, CreatedAt = Utc(2026, 3, 1) },
                new AgentTask { WorkflowRunId = runA, ProjectId = p1, Type = AgentTaskType.ArchitectureDesign, Status = AgentTaskStatus.Completed, RevisionFeedback = "thiếu ERD", CreatedAt = Utc(2026, 3, 1) },
                new AgentTask { WorkflowRunId = runA, ProjectId = p1, Type = AgentTaskType.BugFix, Status = AgentTaskStatus.Completed, CreatedAt = Utc(2026, 3, 1) },
                new AgentTask { WorkflowRunId = runA, ProjectId = p1, Type = AgentTaskType.BugFix, Status = AgentTaskStatus.Completed, CreatedAt = Utc(2026, 3, 1) },
                new AgentTask { WorkflowRunId = runB, ProjectId = p1, Type = AgentTaskType.Implementation, Status = AgentTaskStatus.Failed, CreatedAt = Utc(2026, 3, 2) },
                new AgentTask { WorkflowRunId = runD, ProjectId = p2, Type = AgentTaskType.CodeReview, Status = AgentTaskStatus.Completed, RevisionFeedback = "sửa tên biến", CreatedAt = Utc(2026, 3, 4) });

            // Call logs: cost theo run (WorkflowRunId != null) + 1 call chat BA (run null) cho độ tin cậy model.
            db.AgentModelCallLogs.AddRange(
                new AgentModelCallLog { ProjectId = p1, AgentId = agentId, WorkflowRunId = runA, ModelId = "m1", PromptTokens = 1_000_000, CompletionTokens = 500_000, TotalTokens = 1_500_000, IsSuccess = true, DurationMs = 1000, CreatedAt = Utc(2026, 3, 1) },
                new AgentModelCallLog { ProjectId = p2, AgentId = agentId, WorkflowRunId = runD, ModelId = "m1", PromptTokens = 2_000_000, CompletionTokens = 0, TotalTokens = 2_000_000, IsSuccess = true, DurationMs = 2000, CreatedAt = Utc(2026, 3, 4) },
                new AgentModelCallLog { ProjectId = p1, AgentId = agentId, WorkflowRunId = runB, ModelId = "m1", PromptTokens = 0, CompletionTokens = 0, TotalTokens = 0, IsSuccess = false, DurationMs = 500, CreatedAt = Utc(2026, 3, 2) },
                new AgentModelCallLog { ProjectId = p1, AgentId = agentId, WorkflowRunId = null, ModelId = "m1", PromptTokens = 100_000, CompletionTokens = 0, TotalTokens = 100_000, IsSuccess = true, DurationMs = 300, CreatedAt = Utc(2026, 3, 5) });

            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var vm = await new GetDeliveryQualityQuery(db).ExecuteAsync(2026);

            // Outcomes (run 2023 bị loại → tổng 4).
            Assert.Equal(4, vm.TotalRuns);
            Assert.Equal(2, vm.CompletedRuns);
            Assert.Equal(1, vm.FailedRuns);
            Assert.Equal(0, vm.CanceledRuns);
            Assert.Equal(1, vm.InProgressRuns);
            Assert.Equal(66.7, vm.CompletionRate);   // 2 / 3 đã kết thúc
            Assert.Equal(33.3, vm.FailureRate);
            Assert.Equal(15.0, vm.AvgDurationMinutes); // (10 + 20) / 2

            // Rework.
            Assert.Equal(2, vm.TotalRevisionRequests);
            Assert.Equal(2, vm.TotalBugFixRounds);
            Assert.Equal(2, vm.RunsNeedingRevision);   // A, D
            Assert.Equal(1, vm.RunsNeedingBugFix);      // A
            Assert.Equal(50.0, vm.ReworkRate);          // 2 / 4

            // Cost (chỉ call thuộc run): A = 1*1 + 0.5*2 = 2.0; D = 2*1 = 2.0 ⇒ 4.0.
            Assert.Equal(4.0m, vm.TotalCost);
            Assert.Equal(1.0m, vm.AvgCostPerRun);

            // Failed theo stage.
            var stage = Assert.Single(vm.FailedByStage);
            Assert.Equal(WorkflowStageKey.Implementation, stage.Stage);
            Assert.Equal(1, stage.Count);

            // Theo project.
            Assert.Equal(2, vm.Projects.Count);
            var alpha = vm.Projects.Single(p => p.ProjectName == "Alpha");
            Assert.Equal(3, alpha.Runs);       // A, B, C
            Assert.Equal(1, alpha.Completed);
            Assert.Equal(1, alpha.Failed);
            Assert.Equal(1, alpha.RevisionRequests);
            Assert.Equal(2, alpha.BugFixRounds);
            Assert.Equal(2.0m, alpha.Cost);
            var beta = vm.Projects.Single(p => p.ProjectName == "Beta");
            Assert.Equal(2.0m, beta.Cost);
            Assert.Equal(1, beta.RevisionRequests);

            // Độ tin cậy model: 4 call (gồm chat BA), 3 thành công ⇒ 75%; latency TB (1000+2000+500+300)/4 = 950.
            var m = Assert.Single(vm.Models);
            Assert.Equal(4, m.Calls);
            Assert.Equal(3, m.SuccessCount);
            Assert.Equal(75.0, m.SuccessRate);
            Assert.Equal(950, m.AvgLatencyMs);
            // Cost model = prompt 3.1M * $1 + completion 0.5M * $2 = 3.1 + 1.0 = 4.1.
            Assert.Equal(4.1m, m.Cost);

            Assert.Contains(2026, vm.AvailableYears);
            Assert.Contains(2023, vm.AvailableYears);
        }
    }

    [Fact]
    public async Task EmptyDatabase_ReturnsZeros_NoThrow()
    {
        await using var db = NewDb();
        var vm = await new GetDeliveryQualityQuery(db).ExecuteAsync();

        Assert.Equal(0, vm.TotalRuns);
        Assert.Equal(0, vm.CompletionRate);
        Assert.Equal(0, vm.FailureRate);
        Assert.Null(vm.AvgDurationMinutes);
        Assert.Equal(0m, vm.TotalCost);
        Assert.Empty(vm.Projects);
        Assert.Empty(vm.Models);
        Assert.Empty(vm.FailedByStage);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
