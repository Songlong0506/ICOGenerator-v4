using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

// Nút "Write Requirement" sáng trở lại ngay sau khi POST redirect về trang Requirements (lượt BA mời vẫn
// là lượt cuối vì run mới chỉ vừa xếp hàng), nên người dùng bấm thêm được vài lần. Worker khoá theo project
// nên các run trùng không giẫm file nhau, nhưng vẫn chạy TUẦN TỰ hết — mỗi run là 2–3 lời gọi LLM đắt sinh
// lại đúng bản draft ấy. Cổng gộp bên dưới là chốt chặn THẬT (UI chỉ là lớp che): đã có run đang bay thì
// lần bấm thừa trả về chính run đó.
public class RequirementDraftRunCoalescingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public RequirementDraftRunCoalescingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Queued)]
    [InlineData(WorkflowRunStatus.Running)]
    public async Task Coalesce_WithRunInFlight_ReturnsSameRun_AndAddsNoSecondRun(WorkflowRunStatus status)
    {
        var projectId = await SeedProjectAsync();
        var existingRunId = await SeedDraftRunAsync(projectId, status);

        await using var db = NewDb();
        var runId = await new WorkflowOrchestrator(db).StartRequirementDraftWorkflowAsync(projectId, coalesceWithActiveRun: true);

        Assert.Equal(existingRunId, runId);
        Assert.Equal(1, await db.WorkflowRuns.CountAsync(x => x.ProjectId == projectId));
        // Không có task thứ hai ⇒ worker không có gì để chạy lại: đây mới là thứ tốn token.
        Assert.Equal(0, await db.AgentTasks.CountAsync(x => x.ProjectId == projectId));
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Completed)]
    [InlineData(WorkflowRunStatus.Failed)]
    [InlineData(WorkflowRunStatus.Canceled)]
    public async Task Coalesce_WithOnlyFinishedRuns_StartsNewRun(WorkflowRunStatus status)
    {
        // Vòng trước đã kết thúc ⇒ không còn gì để gộp vào. Người dùng phải soạn lại được (vd sau khi nhắn
        // thêm cho BA, hoặc chạy lại vòng vừa hỏng) — cổng gộp không được biến thành khoá vĩnh viễn.
        var projectId = await SeedProjectAsync();
        var oldRunId = await SeedDraftRunAsync(projectId, status);

        await using var db = NewDb();
        var runId = await new WorkflowOrchestrator(db).StartRequirementDraftWorkflowAsync(projectId, coalesceWithActiveRun: true);

        Assert.NotEqual(oldRunId, runId);
        Assert.Equal(2, await db.WorkflowRuns.CountAsync(x => x.ProjectId == projectId));
        Assert.Equal(1, await db.AgentTasks.CountAsync(x => x.ProjectId == projectId));
    }

    [Fact]
    public async Task WithoutCoalesce_RunInFlight_StillStartsNewRun()
    {
        // Đường "ghi chú trên bản Brief" và "phản hồi POC" VỪA thêm một lượt user vào hội thoại rồi mới gọi
        // vào đây. Run đang bay đã đọc transcript từ trước nên không thấy lượt đó — gộp vào nó là nuốt mất
        // phản hồi người dùng vừa gửi. Hai đường ấy phải luôn được một run mới.
        var projectId = await SeedProjectAsync();
        var existingRunId = await SeedDraftRunAsync(projectId, WorkflowRunStatus.Running);

        await using var db = NewDb();
        var runId = await new WorkflowOrchestrator(db).StartRequirementDraftWorkflowAsync(projectId);

        Assert.NotEqual(existingRunId, runId);
        Assert.Equal(2, await db.WorkflowRuns.CountAsync(x => x.ProjectId == projectId));
    }

    [Fact]
    public async Task Coalesce_IgnoresRunsOfOtherProjects()
    {
        var projectId = await SeedProjectAsync();
        var otherProjectId = await SeedProjectAsync();
        await SeedDraftRunAsync(otherProjectId, WorkflowRunStatus.Running);

        await using var db = NewDb();
        var runId = await new WorkflowOrchestrator(db).StartRequirementDraftWorkflowAsync(projectId, coalesceWithActiveRun: true);

        var run = await db.WorkflowRuns.FirstAsync(x => x.Id == runId);
        Assert.Equal(projectId, run.ProjectId);
        Assert.Equal(1, await db.WorkflowRuns.CountAsync(x => x.ProjectId == projectId));
    }

    [Fact]
    public async Task Coalesce_IgnoresOtherWorkflowKinds()
    {
        // Một dự án đang dựng POC (Delivery Workflow chạy nền) vẫn phải soạn lại Brief được — cổng chỉ
        // gộp đúng vòng "Write Requirement", không phải mọi run đang chạy của project.
        var projectId = await SeedProjectAsync();

        await using (var seed = NewDb())
        {
            seed.WorkflowRuns.Add(new WorkflowRun
            {
                ProjectId = projectId,
                Name = "Delivery Workflow V1",
                Status = WorkflowRunStatus.Running
            });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        var runId = await new WorkflowOrchestrator(db).StartRequirementDraftWorkflowAsync(projectId, coalesceWithActiveRun: true);

        var run = await db.WorkflowRuns.FirstAsync(x => x.Id == runId);
        Assert.Equal(WorkflowOrchestrator.RequirementDraftRunName, run.Name);
        Assert.Equal(2, await db.WorkflowRuns.CountAsync(x => x.ProjectId == projectId));
    }

    [Fact]
    public async Task Coalesce_PicksNewestRun_WhenSeveralAreStillInFlight()
    {
        // Các run trùng đã lọt qua từ trước (dữ liệu cũ, hoặc hai tab cùng qua khe hẹp giữa đọc và ghi):
        // gộp về run MỚI NHẤT để người dùng được dẫn tới đúng panel tiến độ đang chạy trên trang.
        var projectId = await SeedProjectAsync();
        await SeedDraftRunAsync(projectId, WorkflowRunStatus.Running, DateTime.UtcNow.AddMinutes(-5));
        var newestRunId = await SeedDraftRunAsync(projectId, WorkflowRunStatus.Queued, DateTime.UtcNow);

        await using var db = NewDb();
        var runId = await new WorkflowOrchestrator(db).StartRequirementDraftWorkflowAsync(projectId, coalesceWithActiveRun: true);

        Assert.Equal(newestRunId, runId);
    }

    private async Task<Guid> SeedProjectAsync()
    {
        var projectId = Guid.NewGuid();
        await using var db = NewDb();
        db.Projects.Add(new Project { Id = projectId, Name = "P" });
        await db.SaveChangesAsync();
        return projectId;
    }

    private async Task<Guid> SeedDraftRunAsync(Guid projectId, WorkflowRunStatus status, DateTime? createdAt = null)
    {
        var runId = Guid.NewGuid();
        await using var db = NewDb();
        db.WorkflowRuns.Add(new WorkflowRun
        {
            Id = runId,
            ProjectId = projectId,
            Name = WorkflowOrchestrator.RequirementDraftRunName,
            Status = status,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            FinishedAt = status is WorkflowRunStatus.Queued or WorkflowRunStatus.Running ? null : DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return runId;
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
