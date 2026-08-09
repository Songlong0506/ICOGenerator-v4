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
// cùng miền, nên ba điều phải đúng — nhìn thấy được (kèm VÌ SAO rút ra + dự án nguồn), TẮT được từng
// mục, và mục đã tắt phải nằm lại làm danh sách cấm chứ không biến mất.
public class LearnedChecklistTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _baId = Guid.NewGuid();
    private readonly Guid _leaveProjectId = Guid.NewGuid();
    private readonly Guid _commonItemId = Guid.NewGuid();
    private readonly Guid _leaveItemId = Guid.NewGuid();

    public LearnedChecklistTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "test" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });
        db.Projects.Add(new Project { Id = _leaveProjectId, Name = "Nghỉ phép HR", DomainKey = "leave-management" });

        db.AgentChecklistItems.Add(new AgentChecklistItem
        {
            Id = _commonItemId,
            AgentId = _baId,
            DomainKey = null,
            Text = "Hỏi kỹ vòng đời dữ liệu cũ.",
            SourceKind = ChecklistItemSource.Conversation
        });
        db.AgentChecklistItems.Add(new AgentChecklistItem
        {
            Id = _leaveItemId,
            AgentId = _baId,
            DomainKey = "leave-management",
            Text = "Hỏi ai duyệt khi quản lý trực tiếp nghỉ.",
            Rationale = "Người dùng tự nêu người duyệt thay, BA chưa hỏi tới trường hợp người duyệt vắng mặt.",
            Evidence = "sếp em nghỉ thì ai duyệt?",
            SourceKind = ChecklistItemSource.Conversation,
            SourceProjectId = _leaveProjectId
        });
        db.AgentChecklistItems.Add(new AgentChecklistItem
        {
            AgentId = _baId,
            DomainKey = "leave-management",
            Text = "Hỏi cách cộng ngày phép tồn.",
            SourceKind = ChecklistItemSource.PocFeedback,
            SourceProjectId = _leaveProjectId,
            Status = ChecklistItemStatus.DisabledByUser
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Query_GroupsIntoBuckets_WithReasonAndSource()
    {
        await using var db = NewDb();
        var buckets = await new GetLearnedChecklistQuery(db, new BAAgentResolver(db)).ExecuteAsync();

        Assert.Equal(2, buckets.Count);
        Assert.Null(buckets[0].DomainKey); // bucket chung luôn đứng đầu.

        var leave = buckets.Single(b => b.DomainKey == "leave-management");
        Assert.Equal(2, leave.Items.Count);
        Assert.Equal(1, leave.ActiveCount); // mục đã tắt vẫn hiện, chỉ không tính là đang dùng.

        var item = leave.Items.Single(i => i.Id == _leaveItemId);
        Assert.StartsWith("Người dùng tự nêu", item.Rationale);
        Assert.Equal("sếp em nghỉ thì ai duyệt?", item.Evidence);
        // Truy nguồn tới ĐÚNG dự án đã sinh ra bài học, không phải "mọi dự án cùng miền".
        Assert.Equal("Nghỉ phép HR", item.SourceProjectName);
        Assert.Equal("Nghỉ phép HR", Assert.Single(leave.Sources).Name);
    }

    // Bài học đã rút ra là tài sản dùng chung cho mọi dự án SAU — xóa dự án nguồn chỉ được làm mất đường
    // truy nguồn, không được làm mất bài học (FK SetNull).
    [Fact]
    public async Task Query_KeepsLessonAfterSourceProjectDeleted()
    {
        await using (var seed = NewDb())
        {
            seed.Projects.Remove(await seed.Projects.SingleAsync(p => p.Id == _leaveProjectId));
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        var leave = (await new GetLearnedChecklistQuery(db, new BAAgentResolver(db)).ExecuteAsync())
            .Single(b => b.DomainKey == "leave-management");

        var item = leave.Items.Single(i => i.Id == _leaveItemId);
        Assert.Null(item.SourceProjectId);
        Assert.Null(item.SourceProjectName);
        Assert.StartsWith("Người dùng tự nêu", item.Rationale); // lý do vẫn còn để phán đoán.
        Assert.Empty(leave.Sources);
    }

    [Fact]
    public async Task Save_UnticksItem_DisablesIt_ButKeepsItAsBlocklist()
    {
        await using var db = NewDb();
        var result = await NewSave(db).SaveAsync("leave-management", new[]
        {
            new ChecklistItemInput { Id = _leaveItemId, Text = "Hỏi ai duyệt khi quản lý trực tiếp nghỉ.", Enabled = false }
        });

        Assert.Equal(SaveLearnedChecklistResult.Ok, result);
        var saved = await NewDb().AgentChecklistItems.SingleAsync(x => x.Id == _leaveItemId);
        Assert.Equal(ChecklistItemStatus.DisabledByUser, saved.Status);
        Assert.Equal("Hỏi ai duyệt khi quản lý trực tiếp nghỉ.", saved.Text); // vẫn còn nguyên để chặn học lại.
    }

    [Fact]
    public async Task Save_EditsTextInPlace_AndReEnables()
    {
        var disabledId = await NewDb().AgentChecklistItems
            .Where(x => x.Status == ChecklistItemStatus.DisabledByUser)
            .Select(x => x.Id)
            .SingleAsync();

        await using var db = NewDb();
        await NewSave(db).SaveAsync("leave-management", new[]
        {
            new ChecklistItemInput { Id = disabledId, Text = "  Hỏi cách cộng dồn ngày phép tồn cuối năm.  ", Enabled = true }
        });

        var saved = await NewDb().AgentChecklistItems.SingleAsync(x => x.Id == disabledId);
        Assert.Equal("Hỏi cách cộng dồn ngày phép tồn cuối năm.", saved.Text);
        Assert.Equal(ChecklistItemStatus.Active, saved.Status);
    }

    [Fact]
    public async Task Save_EmptyText_KeepsPreviousWording()
    {
        await using var db = NewDb();
        await NewSave(db).SaveAsync(null, new[]
        {
            new ChecklistItemInput { Id = _commonItemId, Text = "   ", Enabled = true }
        });

        var saved = await NewDb().AgentChecklistItems.SingleAsync(x => x.Id == _commonItemId);
        Assert.Equal("Hỏi kỹ vòng đời dữ liệu cũ.", saved.Text);
    }

    [Fact]
    public async Task Save_IgnoresItemsOfAnotherBucket()
    {
        await using var db = NewDb();
        // Id của bucket chung gửi kèm form của bucket miền ⇒ không được đụng tới.
        await NewSave(db).SaveAsync("leave-management", new[]
        {
            new ChecklistItemInput { Id = _commonItemId, Text = "cố sửa xuyên bucket", Enabled = false }
        });

        var untouched = await NewDb().AgentChecklistItems.SingleAsync(x => x.Id == _commonItemId);
        Assert.Equal("Hỏi kỹ vòng đời dữ liệu cũ.", untouched.Text);
        Assert.Equal(ChecklistItemStatus.Active, untouched.Status);
    }

    [Fact]
    public async Task DisableBucket_TurnsOffEveryItem_WithoutDeleting()
    {
        await using var db = NewDb();
        await NewSave(db).DisableBucketAsync("leave-management");

        var items = await NewDb().AgentChecklistItems.Where(x => x.DomainKey == "leave-management").ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.All(items, x => Assert.Equal(ChecklistItemStatus.DisabledByUser, x.Status));
    }

    [Fact]
    public async Task Delete_RemovesItemEntirely()
    {
        await using var db = NewDb();
        await NewSave(db).DeleteAsync(_leaveItemId);

        Assert.False(await NewDb().AgentChecklistItems.AnyAsync(x => x.Id == _leaveItemId));
    }

    [Fact]
    public async Task Save_WithoutBaAgent_ReportsNotConfigured()
    {
        await using (var clean = NewDb())
        {
            clean.AgentChecklistItems.RemoveRange(clean.AgentChecklistItems);
            clean.Agents.RemoveRange(clean.Agents);
            await clean.SaveChangesAsync();
        }

        await using var db = NewDb();
        Assert.Equal(SaveLearnedChecklistResult.BaNotConfigured, await NewSave(db).SaveAsync(null, Array.Empty<ChecklistItemInput>()));
    }

    private static SaveLearnedChecklistUseCase NewSave(AppDbContext db) => new(db, new BAAgentResolver(db));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
