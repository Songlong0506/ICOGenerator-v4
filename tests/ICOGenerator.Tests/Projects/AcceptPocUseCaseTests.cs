using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Notifications;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Nghiệm thu bản demo — trạng thái KẾT của hành trình phía người yêu cầu. Bốn bất biến: ghi đúng ai/lúc
// nào, báo cho người có quyền duyệt, KHÔNG tự đẩy pipeline, và không nghiệm thu được thứ chưa tồn tại
// (hoặc nghiệm thu hai lần — bấm đúp không được ghi đè người/lúc đầu tiên).
public class AcceptPocUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public AcceptPocUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_RecordsWhoAndWhen_AndNotifiesTheDeliveryTeam()
    {
        AddDeliveryRun();
        await using var db = NewDb();
        var notifier = new SpyNotifier();

        var result = await new AcceptPocUseCase(db, notifier).ExecuteAsync(_projectId, "lan.nguyen");

        Assert.Equal(AcceptPocResult.Ok, result);

        var project = await NewDb().Projects.SingleAsync();
        Assert.Equal("lan.nguyen", project.PocAcceptedBy);
        Assert.NotNull(project.PocAcceptedAtUtc);
        Assert.Equal("lan.nguyen", notifier.AcceptedBy);
    }

    // Nghiệm thu là một TÍN HIỆU, không phải một lệnh chạy: cổng POC vẫn phải do người có quyền duyệt
    // bấm. Nếu nó tự đẩy bước, một cú bấm của người dùng nghiệp vụ sẽ khởi động các bước đắt tiền.
    [Fact]
    public async Task ExecuteAsync_DoesNotAdvanceTheWorkflow()
    {
        AddDeliveryRun();
        await using var db = NewDb();

        await new AcceptPocUseCase(db, new SpyNotifier()).ExecuteAsync(_projectId, "lan.nguyen");

        var run = await NewDb().WorkflowRuns.SingleAsync();
        Assert.Equal(WorkflowRunStatus.WaitingForHuman, run.Status);
        Assert.Equal(WorkflowStageKey.PocPreview, run.CurrentStage);
        Assert.Equal(0, await NewDb().AgentTasks.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_Twice_KeepsTheFirstAcceptance()
    {
        AddDeliveryRun();
        await using (var first = NewDb())
            await new AcceptPocUseCase(first, new SpyNotifier()).ExecuteAsync(_projectId, "lan.nguyen");

        await using var db = NewDb();
        var notifier = new SpyNotifier();
        var result = await new AcceptPocUseCase(db, notifier).ExecuteAsync(_projectId, "khac.nguoi");

        Assert.Equal(AcceptPocResult.AlreadyAccepted, result);
        Assert.Equal("lan.nguyen", (await NewDb().Projects.SingleAsync()).PocAcceptedBy);
        Assert.Null(notifier.AcceptedBy);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutADeliveryRun_HasNothingToAccept()
    {
        await using var db = NewDb();

        var result = await new AcceptPocUseCase(db, new SpyNotifier()).ExecuteAsync(_projectId, "lan.nguyen");

        Assert.Equal(AcceptPocResult.NoPoc, result);
        Assert.Null((await NewDb().Projects.SingleAsync()).PocAcceptedAtUtc);
    }

    private void AddDeliveryRun()
    {
        using var db = NewDb();
        db.WorkflowRuns.Add(new WorkflowRun
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            Name = "Delivery Workflow V1",
            Status = WorkflowRunStatus.WaitingForHuman,
            CurrentStage = WorkflowStageKey.PocPreview
        });
        db.SaveChanges();
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class SpyNotifier : INotificationService
    {
        public string? AcceptedBy;

        public Task NotifyGateOpenedAsync(WorkflowRun run, string nextStepTitle, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyRunCompletedAsync(WorkflowRun run, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyRunFailedAsync(WorkflowRun run, string? error, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyPocAcceptedAsync(WorkflowRun run, string acceptedBy, CancellationToken cancellationToken = default)
        {
            AcceptedBy = acceptedBy;
            return Task.CompletedTask;
        }
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
