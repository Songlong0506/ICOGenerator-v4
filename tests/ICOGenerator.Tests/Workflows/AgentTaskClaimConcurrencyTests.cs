using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

// AgentTask.Status là concurrency token — nền tảng cho bước "claim" Queued → Running của
// AgentTaskWorker khi chạy nhiều task song song (Workers:MaxConcurrentAgentTasks > 1): hai bên cùng
// nhặt một task thì bên lưu sau nhận DbUpdateConcurrencyException và bỏ qua, không bao giờ chạy đôi.
public class AgentTaskClaimConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public AgentTaskClaimConcurrencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ClaimingTheSameQueuedTaskTwice_SecondSaveThrows()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            var run = new WorkflowRun { ProjectId = projectId, Status = WorkflowRunStatus.Queued };
            db.WorkflowRuns.Add(run);
            db.AgentTasks.Add(new AgentTask
            {
                Id = taskId,
                WorkflowRunId = run.Id,
                ProjectId = projectId,
                Type = AgentTaskType.PocPreview,
                Status = AgentTaskStatus.Queued,
                Title = "POC"
            });
            await db.SaveChangesAsync();
        }

        // Hai "worker" cùng nạp task Queued rồi cùng claim — mô phỏng hai dispatch chồng nhau.
        await using var worker1 = NewDb();
        await using var worker2 = NewDb();
        var task1 = await worker1.AgentTasks.FirstAsync(x => x.Id == taskId);
        var task2 = await worker2.AgentTasks.FirstAsync(x => x.Id == taskId);

        task1.Status = AgentTaskStatus.Running;
        task1.Attempt += 1;
        await worker1.SaveChangesAsync();

        task2.Status = AgentTaskStatus.Running;
        task2.Attempt += 1;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => worker2.SaveChangesAsync());

        // Bên thắng giữ nguyên kết quả: đúng một lần claim, Attempt chỉ tăng một.
        await using var check = NewDb();
        var task = await check.AgentTasks.FirstAsync(x => x.Id == taskId);
        Assert.Equal(AgentTaskStatus.Running, task.Status);
        Assert.Equal(1, task.Attempt);
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
