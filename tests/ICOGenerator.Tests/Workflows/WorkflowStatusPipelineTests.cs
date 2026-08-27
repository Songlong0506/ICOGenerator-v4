using ICOGenerator.Application.Agents;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

// Dải timeline "Delivery Pipeline" trên Agent Dashboard hiện ở MỌI project, kể cả project vừa tạo
// chưa có lượt chạy nào — người dùng thấy trước lộ trình POC → … → PR thay vì một dashboard trống.
// Server luôn trả đủ các bước theo DeliveryPipeline.Steps để JS chỉ việc vẽ; khi chưa chạy thì mọi
// bước ở trạng thái "pending" và HasWorkflow = false (UI dựa vào đó để ẩn hết nút cổng duyệt).
public class WorkflowStatusPipelineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public WorkflowStatusPipelineTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFullPendingPipeline_WhenProjectHasNoRunYet()
    {
        var projectId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "Project vừa tạo" });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var vm = await NewQuery(db).ExecuteAsync(projectId);

            Assert.False(vm.HasWorkflow);
            Assert.Equal(
                DeliveryPipeline.Steps.Select(s => s.Stage.ToString()),
                vm.Pipeline.Select(p => p.Stage));
            Assert.All(vm.Pipeline, p => Assert.Equal("pending", p.State));
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFullPendingPipeline_WhenOnlyRequirementRunExists()
    {
        // Giai đoạn requirement (chat BA / sinh AI Design Spec) chưa chạy bước delivery nào, nhưng
        // timeline vẫn hiện — chỉ là chưa bước nào sáng lên.
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.WorkflowRuns.Add(new WorkflowRun
            {
                Id = runId,
                ProjectId = projectId,
                Status = WorkflowRunStatus.Running,
                CurrentStage = WorkflowStageKey.RequirementApproved
            });
            db.AgentTasks.Add(new AgentTask
            {
                WorkflowRunId = runId,
                ProjectId = projectId,
                Type = AgentTaskType.RequirementAnalysis,
                Status = AgentTaskStatus.Running,
                Title = "Viết requirement"
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var vm = await NewQuery(db).ExecuteAsync(projectId);

            Assert.Equal("Requirement", vm.RunKind);
            Assert.Equal(DeliveryPipeline.Steps.Count, vm.Pipeline.Count);
            Assert.All(vm.Pipeline, p => Assert.Equal("pending", p.State));
        }
    }

    private static GetWorkflowStatusQuery NewQuery(AppDbContext db) =>
        new(db, new WorkflowProgressReporter());

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
