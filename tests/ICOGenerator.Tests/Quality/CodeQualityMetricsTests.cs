using ICOGenerator.Application.Quality;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Quality;

/// <summary>
/// Khối "Chất lượng code sinh ra" của trang Delivery Quality — BASELINE để so trước–sau mỗi thay đổi ở
/// chặng sinh code (đổi prompt, đổi model, đổi ngân sách bước, hay đổi hẳn engine). Hai luật đọc phải
/// được chốt bằng test vì cả hai đều là chỗ dễ nói dối bằng số:
/// <para>
/// • Mẫu số của tỷ lệ biên dịch chỉ gồm các run CỔNG ĐÃ CHẤM ĐƯỢC. Run cũ (chạy trước khi có cổng) và
/// run bị bỏ qua không được tính là đạt, cũng không bị tính là trượt.
/// </para>
/// <para>
/// • "Xanh" và "xanh NGAY LẦN ĐẦU" là hai cột khác nhau: một run phải sửa ba vòng rồi mới biên dịch
/// được vẫn giao được hàng, nhưng nó không nói lên điều mà cột thứ hai đang đo.
/// </para>
/// </summary>
public class CodeQualityMetricsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public CodeQualityMetricsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static DateTime Utc(int day, int hour = 10) => new(2026, 3, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Measures_First_Pass_Build_Fix_Rounds_Testing_And_Handoff()
    {
        var projectId = Guid.NewGuid();
        var green = Guid.NewGuid();     // xanh ngay lần đầu, test PASS, tới được PR
        var unmeasured = Guid.NewGuid();// chạy trước khi có cổng (output không có dòng BUILD:)
        var fixedUp = Guid.NewGuid();   // đỏ rồi xanh sau một vòng sửa
        var stillRed = Guid.NewGuid();  // hết ngạch vẫn đỏ
        var noCode = Guid.NewGuid();    // chưa từng tới bước sinh code

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "Alpha" });
            db.WorkflowRuns.AddRange(
                NewRun(green, projectId), NewRun(unmeasured, projectId), NewRun(fixedUp, projectId),
                NewRun(stillRed, projectId), NewRun(noCode, projectId));

            db.AgentTasks.AddRange(
                Task_(green, projectId, AgentTaskType.Implementation, "Đã sinh 12 file.\n\nBUILD: PASS", Utc(1)),
                Task_(green, projectId, AgentTaskType.Testing, "Mọi luồng chính đạt.\nVERDICT: PASS", Utc(1, 11)),
                Task_(green, projectId, AgentTaskType.PullRequest, "Đã tạo PR.", Utc(1, 12)),

                Task_(unmeasured, projectId, AgentTaskType.Implementation, "Đã sinh 8 file, chạy bằng dotnet run.", Utc(2)),

                Task_(fixedUp, projectId, AgentTaskType.Implementation, "Đã sinh code.\n\nBUILD: FAIL", Utc(3)),
                Task_(fixedUp, projectId, AgentTaskType.BuildFix, "Đã thêm using còn thiếu.\n\nBUILD: PASS", Utc(3, 11)),

                Task_(stillRed, projectId, AgentTaskType.Implementation, "Đã sinh code.\n\nBUILD: FAIL", Utc(4)),
                Task_(stillRed, projectId, AgentTaskType.BuildFix, "Sửa vòng 1.\n\nBUILD: FAIL", Utc(4, 11)),
                Task_(stillRed, projectId, AgentTaskType.BuildFix, "Sửa vòng 2.\n\nBUILD: FAIL", Utc(4, 12)),
                Task_(stillRed, projectId, AgentTaskType.Testing, "Không build được.\nVERDICT: FAIL", Utc(4, 13)),

                Task_(noCode, projectId, AgentTaskType.PocPreview, "POC xong.", Utc(5)));

            await db.SaveChangesAsync();
        }

        await using var read = NewDb();
        var vm = await new GetDeliveryQualityQuery(read).ExecuteAsync(2026);
        var q = vm.CodeQuality;

        // 4 run đã sinh code (noCode bị loại); chỉ 3 trong số đó cổng chấm được (unmeasured bị loại).
        Assert.Equal(4, q.RunsWithImplementation);
        Assert.Equal(3, q.RunsMeasured);

        Assert.Equal(1, q.RunsCompiledFirstTry);
        Assert.Equal(1, q.RunsStillRed);
        Assert.Equal(33.3, q.FirstPassBuildRate);          // 1/3, KHÔNG phải 1/4

        Assert.Equal(3, q.TotalBuildFixRounds);            // 1 + 2
        Assert.Equal(1, q.AvgBuildFixRounds);              // (0 + 1 + 2)/3

        Assert.Equal(2, q.RunsReachingTesting);
        Assert.Equal(1, q.RunsTestPassed);
        Assert.Equal(50, q.TestPassRate);

        Assert.Equal(1, q.RunsReachingPullRequest);
        Assert.Equal(25, q.PullRequestRate);               // 1/4 run đã sinh code
    }

    // Chưa có dữ liệu thì tỷ lệ phải là NULL để view hiện gạch, không phải 0% — "chưa đo" và "0% đạt"
    // là hai câu khác hẳn nhau về chất lượng.
    [Fact]
    public async Task Reports_Null_Rates_When_Nothing_Has_Been_Measured()
    {
        await using var db = NewDb();
        var vm = await new GetDeliveryQualityQuery(db).ExecuteAsync(2026);

        Assert.Equal(0, vm.CodeQuality.RunsWithImplementation);
        Assert.Null(vm.CodeQuality.FirstPassBuildRate);
        Assert.Null(vm.CodeQuality.AvgBuildFixRounds);
        Assert.Null(vm.CodeQuality.TestPassRate);
        Assert.Null(vm.CodeQuality.PullRequestRate);
    }

    // Task chưa hoàn tất không mang kết luận nào: một bước Implementation Failed không được tính là một
    // run "đã sinh code".
    [Fact]
    public async Task Ignores_Tasks_That_Did_Not_Complete()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "Alpha" });
            db.WorkflowRuns.Add(NewRun(runId, projectId));
            db.AgentTasks.Add(new AgentTask
            {
                WorkflowRunId = runId, ProjectId = projectId, Type = AgentTaskType.Implementation,
                Status = AgentTaskStatus.Failed, Output = "BUILD: PASS", CreatedAt = Utc(1)
            });
            await db.SaveChangesAsync();
        }

        await using var read = NewDb();
        var vm = await new GetDeliveryQualityQuery(read).ExecuteAsync(2026);

        Assert.Equal(0, vm.CodeQuality.RunsWithImplementation);
    }

    private static WorkflowRun NewRun(Guid id, Guid projectId) => new()
    {
        Id = id,
        ProjectId = projectId,
        Status = WorkflowRunStatus.Completed,
        CurrentStage = WorkflowStageKey.Completed,
        CreatedAt = Utc(1)
    };

    private static AgentTask Task_(Guid runId, Guid projectId, AgentTaskType type, string output, DateTime finishedAt) => new()
    {
        WorkflowRunId = runId,
        ProjectId = projectId,
        Type = type,
        Status = AgentTaskStatus.Completed,
        Output = output,
        CreatedAt = finishedAt,
        FinishedAt = finishedAt
    };
}
