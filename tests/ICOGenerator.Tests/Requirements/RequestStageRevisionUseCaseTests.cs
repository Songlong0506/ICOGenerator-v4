using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// "Yêu cầu chỉnh sửa" at an approval gate re-queues the SAME stage with the reviewer's feedback
// instead of canceling the whole run (Reject). The revision task must keep the step's ORIGINAL
// input (spec / previous stage output) — feedback rides on AgentTask.RevisionFeedback — and each
// stage is capped at DeliveryPipeline.MaxRevisionRounds rounds. Unlike Reject, revision is allowed
// at the POC gate too: the feedback there means "POC drifted from the approved spec", not a
// requirement change.
public class RequestStageRevisionUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public RequestStageRevisionUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ExecuteAsync_QueuesRevisionTask_KeepingOriginalInputAndAgent()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.Agents.Add(NewAgent(agentId));
            db.WorkflowRuns.Add(new WorkflowRun
            {
                Id = runId,
                ProjectId = projectId,
                Status = WorkflowRunStatus.WaitingForHuman,
                CurrentStage = WorkflowStageKey.ArchitectureDesign
            });
            db.AgentTasks.Add(new AgentTask
            {
                WorkflowRunId = runId,
                ProjectId = projectId,
                AgentId = agentId,
                Type = AgentTaskType.ArchitectureDesign,
                Status = AgentTaskStatus.Completed,
                Title = "Đề xuất kiến trúc từ AI Design Spec",
                Input = "the original design spec",
                Output = "architecture v1",
                FinishedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var result = await new RequestStageRevisionUseCase(db)
                .ExecuteAsync(projectId, "  Thiếu phần mô hình dữ liệu, bổ sung ERD.  ", runId);
            Assert.Equal(RequestStageRevisionResult.Queued, result);
        }

        await using (var db = NewDb())
        {
            var revision = await db.AgentTasks.SingleAsync(t => t.RevisionFeedback != null);
            Assert.Equal(AgentTaskStatus.Queued, revision.Status);
            Assert.Equal(AgentTaskType.ArchitectureDesign, revision.Type);
            Assert.Equal(agentId, revision.AgentId);
            // Input stays the ORIGINAL step input; the trimmed feedback rides separately.
            Assert.Equal("the original design spec", revision.Input);
            Assert.Equal("Thiếu phần mô hình dữ liệu, bổ sung ERD.", revision.RevisionFeedback);
            Assert.Contains("chỉnh sửa lần 1", revision.Title);

            // Run goes back to the worker at the SAME stage — the gate re-opens after the redo.
            var run = await db.WorkflowRuns.SingleAsync(x => x.Id == runId);
            Assert.Equal(WorkflowRunStatus.Queued, run.Status);
            Assert.Equal(WorkflowStageKey.ArchitectureDesign, run.CurrentStage);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AllowsRevision_AtPocGate()
    {
        // Reject is blocked at the POC gate (PocGateNotRejectable), but revision is allowed:
        // it fixes the POC against the approved spec instead of changing the requirement.
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
            db.AgentTasks.Add(new AgentTask
            {
                WorkflowRunId = runId,
                ProjectId = projectId,
                Type = AgentTaskType.PocPreview,
                Status = AgentTaskStatus.Completed,
                Title = "Tạo POC HTML để xem trước",
                Input = "spec",
                FinishedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var result = await new RequestStageRevisionUseCase(db)
                .ExecuteAsync(projectId, "Màn hình danh sách thiếu cột trạng thái như spec.", runId);
            Assert.Equal(RequestStageRevisionResult.Queued, result);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsMissingFeedback_AndChangesNothing_WhenFeedbackBlank()
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
            var result = await new RequestStageRevisionUseCase(db).ExecuteAsync(projectId, "   ", runId);
            Assert.Equal(RequestStageRevisionResult.MissingFeedback, result);
        }

        await using (var db = NewDb())
        {
            Assert.Equal(0, await db.AgentTasks.CountAsync());
            var run = await db.WorkflowRuns.SingleAsync(x => x.Id == runId);
            Assert.Equal(WorkflowRunStatus.WaitingForHuman, run.Status);
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
        var result = await new RequestStageRevisionUseCase(readDb).ExecuteAsync(projectId, "feedback");

        Assert.Equal(RequestStageRevisionResult.NoWaitingRun, result);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNoWaitingRun_WhenStageHasNoCompletedTask()
    {
        // Defensive: a waiting run whose current stage has no completed task is in an abnormal
        // state — there is nothing to revise from.
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

        await using var readDb = NewDb();
        var result = await new RequestStageRevisionUseCase(readDb).ExecuteAsync(projectId, "feedback", runId);

        Assert.Equal(RequestStageRevisionResult.NoWaitingRun, result);
    }

    [Fact]
    public async Task ExecuteAsync_SecondRevision_KeepsOriginalInput_AndNumbersTheRound()
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
                CurrentStage = WorkflowStageKey.Implementation
            });
            db.AgentTasks.Add(new AgentTask
            {
                WorkflowRunId = runId,
                ProjectId = projectId,
                Type = AgentTaskType.Implementation,
                Status = AgentTaskStatus.Completed,
                Title = "Sinh code đầy đủ từ kiến trúc",
                Input = "approved architecture",
                FinishedAt = DateTime.UtcNow.AddMinutes(-10)
            });
            // Round 1 already done — its input must equal the original's, and round 2 counts on.
            db.AgentTasks.Add(new AgentTask
            {
                WorkflowRunId = runId,
                ProjectId = projectId,
                Type = AgentTaskType.Implementation,
                Status = AgentTaskStatus.Completed,
                Title = "Sinh code đầy đủ từ kiến trúc (chỉnh sửa lần 1)",
                Input = "approved architecture",
                RevisionFeedback = "round 1 feedback",
                FinishedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var result = await new RequestStageRevisionUseCase(db).ExecuteAsync(projectId, "round 2 feedback", runId);
            Assert.Equal(RequestStageRevisionResult.Queued, result);
        }

        await using (var db = NewDb())
        {
            var round2 = await db.AgentTasks.SingleAsync(t => t.RevisionFeedback == "round 2 feedback");
            Assert.Equal("approved architecture", round2.Input);
            Assert.Contains("chỉnh sửa lần 2", round2.Title);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RefusesAndKeepsRun_WhenRevisionRoundsExhausted()
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
                CurrentStage = WorkflowStageKey.CodeReview
            });
            db.AgentTasks.Add(new AgentTask
            {
                WorkflowRunId = runId,
                ProjectId = projectId,
                Type = AgentTaskType.CodeReview,
                Status = AgentTaskStatus.Completed,
                Title = "Review code đã hiện thực",
                Input = "handoff",
                FinishedAt = DateTime.UtcNow.AddMinutes(-30)
            });
            for (var round = 1; round <= DeliveryPipeline.MaxRevisionRounds; round++)
            {
                db.AgentTasks.Add(new AgentTask
                {
                    WorkflowRunId = runId,
                    ProjectId = projectId,
                    Type = AgentTaskType.CodeReview,
                    Status = AgentTaskStatus.Completed,
                    Title = $"Review code đã hiện thực (chỉnh sửa lần {round})",
                    Input = "handoff",
                    RevisionFeedback = $"feedback {round}",
                    FinishedAt = DateTime.UtcNow.AddMinutes(-30 + round)
                });
            }
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var result = await new RequestStageRevisionUseCase(db).ExecuteAsync(projectId, "one more", runId);
            Assert.Equal(RequestStageRevisionResult.RevisionLimitReached, result);
        }

        await using (var db = NewDb())
        {
            // Nothing was queued and the gate stays open — the reviewer must Approve or Reject.
            Assert.Equal(1 + DeliveryPipeline.MaxRevisionRounds, await db.AgentTasks.CountAsync());
            var run = await db.WorkflowRuns.SingleAsync(x => x.Id == runId);
            Assert.Equal(WorkflowRunStatus.WaitingForHuman, run.Status);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AtPocGate_MergesPinnedComments_AndMarksThemSent()
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
            db.AgentTasks.Add(new AgentTask
            {
                WorkflowRunId = runId,
                ProjectId = projectId,
                Type = AgentTaskType.PocPreview,
                Status = AgentTaskStatus.Completed,
                Title = "Tạo POC HTML để xem trước",
                Input = "spec",
                FinishedAt = DateTime.UtcNow
            });
            db.PocComments.AddRange(
                new PocComment
                {
                    ProjectId = projectId,
                    PageView = "Overview",
                    ElementLabel = "Nút “Save”",
                    ElementPath = "#main > button:nth-of-type(2)",
                    Comment = "Đổi nhãn thành 'Lưu'",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-2)
                },
                // Ghi chú đã Sent ở vòng trước — KHÔNG được gửi lặp.
                new PocComment
                {
                    ProjectId = projectId,
                    Comment = "đã gửi vòng trước",
                    Status = PocCommentStatus.Sent,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-9)
                });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            // Nhận xét gõ tay được phép TRỐNG khi có ghi chú ghim được gửi kèm.
            var result = await new RequestStageRevisionUseCase(db)
                .ExecuteAsync(projectId, "  ", runId, includePocComments: true);
            Assert.Equal(RequestStageRevisionResult.Queued, result);
        }

        await using (var db = NewDb())
        {
            var revision = await db.AgentTasks.SingleAsync(t => t.RevisionFeedback != null);
            Assert.Contains("Ghi chú ghim trực tiếp trên POC", revision.RevisionFeedback);
            Assert.Contains("Màn hình \"Overview\"", revision.RevisionFeedback);
            Assert.Contains("Đổi nhãn thành 'Lưu'", revision.RevisionFeedback);
            Assert.Contains("selector: #main > button:nth-of-type(2)", revision.RevisionFeedback);
            Assert.DoesNotContain("đã gửi vòng trước", revision.RevisionFeedback);

            // Ghi chú Open đã gom → Sent để vòng chỉnh sửa sau không gửi lặp.
            Assert.Equal(PocCommentStatus.Sent,
                (await db.PocComments.SingleAsync(c => c.Comment == "Đổi nhãn thành 'Lưu'")).Status);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AtPocGate_NoOpenComments_AndBlankFeedback_ReturnsMissingFeedback()
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

        await using var readDb = NewDb();
        var result = await new RequestStageRevisionUseCase(readDb)
            .ExecuteAsync(projectId, "", runId, includePocComments: true);

        Assert.Equal(RequestStageRevisionResult.MissingFeedback, result);
    }

    [Fact]
    public async Task ExecuteAsync_NotPocGate_IgnoresIncludeFlag_AndKeepsCommentsOpen()
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
            db.AgentTasks.Add(new AgentTask
            {
                WorkflowRunId = runId,
                ProjectId = projectId,
                Type = AgentTaskType.ArchitectureDesign,
                Status = AgentTaskStatus.Completed,
                Title = "Đề xuất kiến trúc",
                Input = "spec",
                FinishedAt = DateTime.UtcNow
            });
            db.PocComments.Add(new PocComment { ProjectId = projectId, Comment = "ghi chú POC" });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var result = await new RequestStageRevisionUseCase(db)
                .ExecuteAsync(projectId, "sửa kiến trúc", runId, includePocComments: true);
            Assert.Equal(RequestStageRevisionResult.Queued, result);
        }

        await using (var db = NewDb())
        {
            var revision = await db.AgentTasks.SingleAsync(t => t.RevisionFeedback != null);
            // Ghi chú POC thuộc cổng POC — bước khác không được "mượn" (và cũng không bị đốt).
            Assert.DoesNotContain("ghi chú POC", revision.RevisionFeedback);
            Assert.Equal(PocCommentStatus.Open, (await db.PocComments.SingleAsync()).Status);
        }
    }

    // AgentTask.AgentId is a real FK — tests that assert agent preservation need a seeded agent
    // (with its own AiModel, also a required FK).
    private static Agent NewAgent(Guid id) => new()
    {
        Id = id,
        Name = "Tech Lead",
        RoleKey = AgentRoleKey.TechLead,
        AiModel = new AiModel { Name = "test-model" }
    };

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
