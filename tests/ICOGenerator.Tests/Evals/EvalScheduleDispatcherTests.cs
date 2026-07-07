using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Evals;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// EvalScheduleDispatcher: lịch đến hạn ⇒ tạo run Queued mang ScheduleId + snapshot tên model MỚI NHẤT,
// rồi LUÔN dời NextRunAt tới (now + interval) — kể cả khi lượt bị bỏ qua (run cũ chưa xong / model tắt /
// không còn scenario khớp) để lịch hỏng không retry dồn dập. Chạy trên AppDbContext thật (Sqlite).
public class EvalScheduleDispatcherTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _targetModelId = Guid.NewGuid();
    private readonly Guid _judgeModelId = Guid.NewGuid();

    public EvalScheduleDispatcherTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.AddRange(
            new AiModel { Id = _targetModelId, Name = "Target v2", ModelId = "target" },
            new AiModel { Id = _judgeModelId, Name = "Judge", ModelId = "judge" });
        db.EvalScenarios.AddRange(
            new EvalScenario { Name = "S1", PromptKey = "BA/x.md", UserInput = "in", Criteria = "c" },
            new EvalScenario { Name = "S2", PromptKey = "BA/y.md", UserInput = "in", Criteria = "c" });
        db.SaveChanges();
    }

    [Fact]
    public async Task DispatchDue_EnqueuesRunAndAdvancesNextRunAt()
    {
        var now = DateTime.UtcNow;
        var scheduleId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.EvalSchedules.Add(NewSchedule(scheduleId, now.AddMinutes(-5), intervalHours: 6, promptKey: "BA/x.md",
                // Snapshot tên trên lịch đã CŨ — run phải mang tên model hiện hành.
                targetModelName: "Target v1"));
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var enqueued = await new EvalScheduleDispatcher(db, NullLogger<EvalScheduleDispatcher>.Instance)
                .DispatchDueAsync(now);
            Assert.Equal(1, enqueued);
        }

        await using (var verify = NewDb())
        {
            var run = await verify.EvalRuns.SingleAsync();
            Assert.Equal(EvalRunStatus.Queued, run.Status);
            Assert.Equal(scheduleId, run.ScheduleId);
            Assert.Equal("Target v2", run.TargetModelName); // snapshot tươi lúc enqueue
            Assert.Equal("BA/x.md", run.PromptKey);
            Assert.Equal(1, run.ScenarioCount); // chỉ scenario khớp bộ lọc
            Assert.Contains("Lịch đêm", run.Note);
            Assert.Equal("teamdev", run.CreatedByUsername);

            var schedule = await verify.EvalSchedules.SingleAsync();
            Assert.Equal(now.AddHours(6), schedule.NextRunAt, TimeSpan.FromSeconds(1));
            Assert.NotNull(schedule.LastEnqueuedAt);
        }
    }

    [Fact]
    public async Task DispatchDue_NotDueOrDisabled_DoesNothing()
    {
        var now = DateTime.UtcNow;
        await using (var db = NewDb())
        {
            db.EvalSchedules.Add(NewSchedule(Guid.NewGuid(), now.AddHours(1))); // chưa đến hạn
            var disabled = NewSchedule(Guid.NewGuid(), now.AddHours(-1));
            disabled.IsEnabled = false;
            db.EvalSchedules.Add(disabled);
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
            Assert.Equal(0, await new EvalScheduleDispatcher(db, NullLogger<EvalScheduleDispatcher>.Instance).DispatchDueAsync(now));

        await using (var verify = NewDb())
            Assert.Equal(0, await verify.EvalRuns.CountAsync());
    }

    [Fact]
    public async Task DispatchDue_PreviousRunStillUnfinished_SkipsButStillAdvances()
    {
        var now = DateTime.UtcNow;
        var scheduleId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.EvalSchedules.Add(NewSchedule(scheduleId, now.AddMinutes(-1), intervalHours: 2));
            db.EvalRuns.Add(new EvalRun
            {
                ScheduleId = scheduleId,
                Status = EvalRunStatus.Running,
                TargetModelId = _targetModelId,
                JudgeModelId = _judgeModelId
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
            Assert.Equal(0, await new EvalScheduleDispatcher(db, NullLogger<EvalScheduleDispatcher>.Instance).DispatchDueAsync(now));

        await using (var verify = NewDb())
        {
            Assert.Equal(1, await verify.EvalRuns.CountAsync()); // không xếp chồng run mới
            var schedule = await verify.EvalSchedules.SingleAsync();
            Assert.Equal(now.AddHours(2), schedule.NextRunAt, TimeSpan.FromSeconds(1)); // hạn vẫn dời tới
        }
    }

    [Fact]
    public async Task DispatchDue_InactiveModelOrNoScenario_SkipsButStillAdvances()
    {
        var now = DateTime.UtcNow;
        var badModel = NewSchedule(Guid.NewGuid(), now.AddMinutes(-1));
        badModel.TargetModelId = Guid.NewGuid(); // model không tồn tại
        var noScenario = NewSchedule(Guid.NewGuid(), now.AddMinutes(-1), promptKey: "BA/khong-co.md");

        await using (var db = NewDb())
        {
            db.EvalSchedules.AddRange(badModel, noScenario);
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
            Assert.Equal(0, await new EvalScheduleDispatcher(db, NullLogger<EvalScheduleDispatcher>.Instance).DispatchDueAsync(now));

        await using (var verify = NewDb())
        {
            Assert.Equal(0, await verify.EvalRuns.CountAsync());
            Assert.All(await verify.EvalSchedules.ToListAsync(),
                s => Assert.True(s.NextRunAt > now)); // cả hai lịch đều được dời hạn
        }
    }

    private EvalSchedule NewSchedule(Guid id, DateTime nextRunAt, int intervalHours = 24, string? promptKey = null, string targetModelName = "Target v2") => new()
    {
        Id = id,
        Name = "Lịch đêm",
        PromptKey = promptKey,
        TargetModelId = _targetModelId,
        TargetModelName = targetModelName,
        JudgeModelId = _judgeModelId,
        JudgeModelName = "Judge",
        IntervalHours = intervalHours,
        NextRunAt = nextRunAt,
        CreatedByUsername = "teamdev"
    };

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
