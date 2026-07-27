using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Sửa TAY panel "Ví dụ đã xác nhận" — oracle mà POC bị chấm theo (spec đúc thành "## 13. Worked
// Examples", POC phải tính lại ra đúng kết quả). Hai bất biến: lưu đúng định dạng bullet mà
// InterviewOutlookService.ParseItems đọc lại được, và LUÔN ghi kèm một lượt hội thoại — không có nó,
// lượt chắt lọc sau mỗi lượt chat sẽ viết đè bản sửa tay về cách hiểu cũ.
public class UpdateWorkedExamplesUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public UpdateWorkedExamplesUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "m" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "P",
            WorkedExamples = "- 3 mục tiêu 80/90/70, trọng số 50/30/20 => 80"
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_SavesBulletsReadableByTheOutlookParser()
    {
        await using var db = NewDb();

        var result = await NewSut(db).ExecuteAsync(_projectId, new[]
        {
            "3 mục tiêu 80/90/70, trọng số 50/30/20 => 81",
            "- nhân viên gửi đơn rồi quản lý duyệt => Đã duyệt (khóa sửa)"
        });

        Assert.Equal(UpdateWorkedExamplesResult.Ok, result);

        await using var verify = NewDb();
        var stored = (await verify.Projects.SingleAsync()).WorkedExamples;
        var parsed = InterviewOutlookService.ParseItems(stored);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("3 mục tiêu 80/90/70, trọng số 50/30/20 => 81", parsed[0]);
        // Gạch đầu dòng người dùng tự gõ không được nhân đôi thành "- - …".
        Assert.Equal("- nhân viên gửi đơn rồi quản lý duyệt => Đã duyệt (khóa sửa)", parsed[1]);
    }

    [Fact]
    public async Task ExecuteAsync_AlsoRecordsAConversationTurn_SoTheNextDistillDoesNotOverwriteIt()
    {
        await using var db = NewDb();

        await NewSut(db).ExecuteAsync(_projectId, new[] { "tổng 3 mục tiêu => 81" });

        await using var verify = NewDb();
        var turn = await verify.AgentConversations.SingleAsync();
        Assert.Equal("user", turn.Role);
        Assert.Contains("tổng 3 mục tiêu => 81", turn.Message);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyList_ClearsTheOracle()
    {
        await using var db = NewDb();

        var result = await NewSut(db).ExecuteAsync(_projectId, Array.Empty<string>());

        Assert.Equal(UpdateWorkedExamplesResult.Ok, result);
        Assert.Null((await NewDb().Projects.SingleAsync()).WorkedExamples);
    }

    [Fact]
    public async Task ExecuteAsync_TooManyExamples_IsRejectedWithoutTouchingAnything()
    {
        await using var db = NewDb();

        var result = await NewSut(db).ExecuteAsync(
            _projectId, Enumerable.Range(1, 31).Select(i => $"ví dụ {i}").ToList());

        Assert.Equal(UpdateWorkedExamplesResult.TooMany, result);
        Assert.Contains("=> 80", (await NewDb().Projects.SingleAsync()).WorkedExamples);
        Assert.Equal(0, await NewDb().AgentConversations.CountAsync());
    }

    private static UpdateWorkedExamplesUseCase NewSut(AppDbContext db) =>
        new(db, new BAConversationLog(db), new BAAgentResolver(db));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
