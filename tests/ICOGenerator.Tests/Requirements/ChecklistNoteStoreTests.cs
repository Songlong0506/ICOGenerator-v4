using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Kho "checklist học được" theo từng mục. Các test chốt đúng những luật khiến kho này khác blob text cũ:
// chỉ mục ĐANG DÙNG đi vào prompt (lý do/bằng chứng thì không), bài học người dùng đã tắt KHÔNG được học
// lại, bucket miền không rò sang dự án miền khác, và trần số mục được thực thi công khai (tắt chứ không
// xóa) thay vì nhờ LLM âm thầm bỏ bớt.
public class ChecklistNoteStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Agent _ba = new() { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst };

    public ChecklistNoteStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "test" };
        db.AiModels.Add(model);
        _ba.AiModelId = model.Id;
        db.Agents.Add(_ba);
        db.SaveChanges();
    }

    [Fact]
    public async Task BuildForChat_TakesActiveItemsOfCommonAndDomainBucketOnly()
    {
        await SeedItemsAsync(
            (null, "Mục chung đang dùng.", ChecklistItemStatus.Active),
            (null, "Mục chung đã tắt.", ChecklistItemStatus.DisabledByUser),
            ("leave-management", "Mục nghỉ phép đang dùng.", ChecklistItemStatus.Active),
            ("inventory", "Mục kho — miền khác.", ChecklistItemStatus.Active));

        await using var db = NewDb();
        var prompt = await new ChecklistNoteStore(db).BuildForChatAsync(_ba, "leave-management");

        Assert.Contains("- Mục chung đang dùng.", prompt);
        Assert.Contains("- Mục nghỉ phép đang dùng.", prompt);
        Assert.DoesNotContain("đã tắt", prompt);
        Assert.DoesNotContain("miền khác", prompt);
    }

    // Lý do và bằng chứng chỉ để người quản trị đọc: nhồi chúng vào prompt là bắt MỌI lượt chat của mọi
    // dự án cùng bucket trả token cho phần BA không dùng.
    [Fact]
    public async Task BuildForChat_NeverLeaksRationaleOrEvidence()
    {
        await using (var seed = NewDb())
        {
            seed.AgentChecklistItems.Add(new AgentChecklistItem
            {
                AgentId = _ba.Id,
                Text = "Hỏi về lưu vết thao tác.",
                Rationale = "Người dùng tự nêu nhu cầu kiểm toán mà BA chưa hỏi.",
                Evidence = "cuối năm kiểm toán hay hỏi"
            });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        var prompt = await new ChecklistNoteStore(db).BuildForChatAsync(_ba, null);

        Assert.Equal("- Hỏi về lưu vết thao tác.", prompt);
    }

    [Fact]
    public async Task BuildForChat_NoActiveItem_ReturnsNull()
    {
        await SeedItemsAsync((null, "Chỉ có mục đã tắt.", ChecklistItemStatus.DisabledByUser));

        await using var db = NewDb();
        Assert.Null(await new ChecklistNoteStore(db).BuildForChatAsync(_ba, "leave-management"));
    }

    [Fact]
    public async Task BuildForChat_OverCharBudget_StopsAtItemBoundary()
    {
        var longText = new string('x', 390);
        await SeedItemsAsync(Enumerable.Range(0, 25).Select(i => ((string?)null, $"{i:D2} {longText}", ChecklistItemStatus.Active)).ToArray());

        await using var db = NewDb();
        var prompt = await new ChecklistNoteStore(db).BuildForChatAsync(_ba, null);

        Assert.True(prompt!.Length <= 4000, $"prompt dài {prompt.Length} ký tự");
        // Mọi dòng lọt vào phải NGUYÊN VẸN — blob cũ cắt cứng ở ký tự thứ 4000, cụt giữa câu.
        Assert.All(prompt.Split('\n'), line => Assert.Equal(2 + 3 + longText.Length, line.Length));
    }

    [Fact]
    public void MergeHarvest_AddsLessonWithReasonAndSource()
    {
        var projectId = Guid.NewGuid();
        using var db = NewDb();
        db.Projects.Add(new Project { Id = projectId, Name = "P" });
        db.SaveChanges();

        var store = new ChecklistNoteStore(db);
        var existing = new List<AgentChecklistItem>();
        var added = store.MergeHarvest(_ba, "leave-management", existing, new[]
        {
            new ChecklistLesson { Text = "- Hỏi ai duyệt khi quản lý vắng.", Rationale = "BA chưa hỏi người duyệt thay.", Evidence = "sếp em nghỉ thì ai duyệt?" }
        }, ChecklistItemSource.Conversation, projectId);
        db.SaveChanges();

        Assert.Equal(1, added);
        var item = db.AgentChecklistItems.Single();
        Assert.Equal("Hỏi ai duyệt khi quản lý vắng.", item.Text); // gạch đầu dòng của model bị bóc.
        Assert.Equal("leave-management", item.DomainKey);
        Assert.Equal(projectId, item.SourceProjectId);
    }

    [Theory]
    [InlineData("Hỏi ai duyệt khi quản lý vắng.")]
    [InlineData("hỏi ai duyệt khi quản lý vắng")] // khác hoa/thường + dấu câu
    [InlineData("- Hỏi  ai  duyệt   khi quản lý vắng!")] // khác khoảng trắng + gạch đầu dòng
    public async Task MergeHarvest_DoesNotRelearnDisabledLesson(string reworded)
    {
        await SeedItemsAsync((null, "Hỏi ai duyệt khi quản lý vắng.", ChecklistItemStatus.DisabledByUser));

        await using var db = NewDb();
        var store = new ChecklistNoteStore(db);
        var existing = await store.LoadBucketAsync(_ba, null);
        var added = store.MergeHarvest(_ba, null, existing, new[] { new ChecklistLesson { Text = reworded } }, ChecklistItemSource.Conversation, null);
        await db.SaveChangesAsync();

        Assert.Equal(0, added);
        Assert.Equal(ChecklistItemStatus.DisabledByUser, (await NewDb().AgentChecklistItems.SingleAsync()).Status);
    }

    [Fact]
    public async Task MergeHarvest_TakesAtMostFiveLessonsPerRound()
    {
        await using var db = NewDb();
        var store = new ChecklistNoteStore(db);
        var lessons = Enumerable.Range(0, 9).Select(i => new ChecklistLesson { Text = $"Bài học số {i}." }).ToList();

        var added = store.MergeHarvest(_ba, null, new List<AgentChecklistItem>(), lessons, ChecklistItemSource.Conversation, null);
        await db.SaveChangesAsync();

        Assert.Equal(ChecklistNoteStore.MaxLessonsPerHarvest, added);
    }

    // Trần số mục được thực thi CÔNG KHAI: mục cũ nhất chuyển sang "tự tắt" (vẫn thấy, bật lại được),
    // thay cho việc blob cũ nhờ LLM "bỏ bớt mục ít giá trị" — âm thầm và không hoàn tác được.
    [Fact]
    public async Task MergeHarvest_OverBucketCap_AutoDisablesOldestActiveItems()
    {
        var cap = ChecklistNoteStore.MaxActiveItemsPerBucket;
        await SeedItemsAsync(Enumerable.Range(0, cap).Select(i => ((string?)null, $"Mục cũ {i:D2}.", ChecklistItemStatus.Active)).ToArray());

        await using var db = NewDb();
        var store = new ChecklistNoteStore(db);
        var existing = await store.LoadBucketAsync(_ba, null);
        store.MergeHarvest(_ba, null, existing, new[]
        {
            new ChecklistLesson { Text = "Bài học mới nhất." },
            new ChecklistLesson { Text = "Bài học mới nhì." }
        }, ChecklistItemSource.Conversation, null);
        await db.SaveChangesAsync();

        var items = await NewDb().AgentChecklistItems.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync();
        Assert.Equal(cap + 2, items.Count); // không mục nào bị xóa.
        Assert.Equal(cap, items.Count(x => x.Status == ChecklistItemStatus.Active));
        Assert.Equal("Mục cũ 00.", items[0].Text);
        Assert.Equal(ChecklistItemStatus.DisabledByOverflow, items[0].Status);
        Assert.Equal(ChecklistItemStatus.DisabledByOverflow, items[1].Status);
        Assert.All(items.TakeLast(2), x => Assert.Equal(ChecklistItemStatus.Active, x.Status));
    }

    [Fact]
    public async Task RenderContextForHarvest_SeparatesActiveFromBlocklist()
    {
        await SeedItemsAsync(
            (null, "Mục đang dùng.", ChecklistItemStatus.Active),
            (null, "Mục người dùng đã tắt.", ChecklistItemStatus.DisabledByUser));

        await using var db = NewDb();
        var context = ChecklistNoteStore.RenderContextForHarvest(await new ChecklistNoteStore(db).LoadBucketAsync(_ba, null));

        var activeAt = context.IndexOf("Mục đang dùng.", StringComparison.Ordinal);
        var blockedAt = context.IndexOf("Mục người dùng đã tắt.", StringComparison.Ordinal);
        Assert.True(activeAt < context.IndexOf("đã bị loại", StringComparison.Ordinal));
        Assert.True(blockedAt > context.IndexOf("đã bị loại", StringComparison.Ordinal));
    }

    private async Task SeedItemsAsync(params (string? DomainKey, string Text, ChecklistItemStatus Status)[] items)
    {
        await using var db = NewDb();
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < items.Length; i++)
        {
            db.AgentChecklistItems.Add(new AgentChecklistItem
            {
                AgentId = _ba.Id,
                DomainKey = items[i].DomainKey,
                Text = items[i].Text,
                Status = items[i].Status,
                CreatedAt = createdAt.AddSeconds(i),
                UpdatedAt = createdAt.AddSeconds(i)
            });
        }
        await db.SaveChangesAsync();
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
