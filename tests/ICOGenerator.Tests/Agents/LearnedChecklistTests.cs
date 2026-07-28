using ICOGenerator.Application.Agents;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Agents;

// Trang quản trị "checklist BA học được": nội dung này được nạp vào prompt ở MỌI lượt chat của các dự án
// cùng miền, nên hai điều phải đúng — nhìn thấy được (kèm dự án nào đã đóng góp) và GỠ được.
public class LearnedChecklistTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _baId = Guid.NewGuid();

    public LearnedChecklistTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "test" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent
        {
            Id = _baId,
            RoleKey = AgentRoleKey.BusinessAnalyst,
            AiModelId = model.Id,
            LearnedChecklistNotes = "- Hỏi kỹ vòng đời dữ liệu cũ."
        });
        db.AgentDomainChecklistNotes.Add(new AgentDomainChecklistNote
        {
            AgentId = _baId,
            DomainKey = "leave-management",
            Notes = "- Hỏi ai duyệt khi quản lý trực tiếp nghỉ.\n- Hỏi cách cộng ngày phép tồn."
        });
        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "Nghỉ phép HR", DomainKey = "leave-management", ChecklistGapHarvested = true });
        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "Kho vật tư", DomainKey = "inventory", PocFeedbackHarvestedCount = 3 });
        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "Chưa harvest", DomainKey = "leave-management" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Query_ListsBucketsWithItemCountsAndSources()
    {
        await using var db = NewDb();
        var buckets = await new GetLearnedChecklistQuery(db, new BAAgentResolver(db)).ExecuteAsync();

        Assert.Equal(2, buckets.Count);

        var common = buckets.Single(b => b.DomainKey == null);
        Assert.Equal(1, common.ItemCount);

        var leave = buckets.Single(b => b.DomainKey == "leave-management");
        Assert.Equal(2, leave.ItemCount);
        // Chỉ dự án ĐÃ chạy harvest mới là nguồn — dự án cùng miền nhưng chưa harvest không đóng góp gì.
        Assert.Single(leave.Sources);
        Assert.Equal("Nghỉ phép HR", leave.Sources[0].Name);
    }

    [Fact]
    public async Task Update_EditsBucketInPlace()
    {
        await using var db = NewDb();
        var result = await NewUpdate(db).ExecuteAsync("leave-management", "- Chỉ còn một mục.");

        Assert.Equal(UpdateLearnedChecklistResult.Ok, result);
        var row = await NewDb().AgentDomainChecklistNotes.SingleAsync(x => x.DomainKey == "leave-management");
        Assert.Equal("- Chỉ còn một mục.", row.Notes);
    }

    [Fact]
    public async Task Update_EmptyNotes_RemovesBucketEntirely()
    {
        await using var db = NewDb();
        await NewUpdate(db).ExecuteAsync("leave-management", "   ");

        Assert.False(await NewDb().AgentDomainChecklistNotes.AnyAsync(x => x.DomainKey == "leave-management"));
    }

    [Fact]
    public async Task Update_EmptyNotesOnCommonBucket_ClearsAgentColumn()
    {
        await using var db = NewDb();
        await NewUpdate(db).ExecuteAsync(null, "");

        Assert.Null((await NewDb().Agents.SingleAsync(a => a.Id == _baId)).LearnedChecklistNotes);
    }

    [Fact]
    public async Task Update_WithoutBaAgent_ReportsNotConfigured()
    {
        await using (var clean = NewDb())
        {
            clean.Agents.RemoveRange(clean.Agents);
            await clean.SaveChangesAsync();
        }

        await using var db = NewDb();
        Assert.Equal(UpdateLearnedChecklistResult.BaNotConfigured, await NewUpdate(db).ExecuteAsync(null, "gì đó"));
    }

    private static UpdateLearnedChecklistUseCase NewUpdate(AppDbContext db) =>
        new(db, new ChecklistNoteStore(db), new BAAgentResolver(db));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
