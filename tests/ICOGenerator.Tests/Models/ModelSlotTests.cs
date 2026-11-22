using ICOGenerator.Application.Models;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Models;

// SUẤT MODEL dùng chung (màn AI Models → "Model theo vai trò"): một dòng mỗi suất, không có dòng = lời gọi chạy
// model của agent gọi nó. Chạy trên AppDbContext thật (Sqlite in-memory) vì phần đáng ghim là ràng buộc DB:
// khoá chính theo suất, và xoá model thì dòng đi theo (Cascade) chứ không chặn việc xoá.
public class ModelSlotTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _activeId = Guid.NewGuid();
    private readonly Guid _otherId = Guid.NewGuid();
    private readonly Guid _inactiveId = Guid.NewGuid();

    public ModelSlotTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.AddRange(
            new AiModel { Id = _activeId, ModelId = "haiku", Endpoint = "http://x", ApiKey = "k", IsActive = true },
            new AiModel { Id = _otherId, ModelId = "sonnet", Endpoint = "http://x", ApiKey = "k", IsActive = true },
            new AiModel { Id = _inactiveId, ModelId = "old", Endpoint = "http://x", ApiKey = "k", IsActive = false });
        db.SaveChanges();
    }

    [Fact]
    public async Task Resolver_ReturnsNull_WhenTheSlotIsNotConfigured()
    {
        await using var db = NewDb();
        Assert.Null(await new ModelSlotResolver(db).FindAsync(ModelSlot.Judge));
    }

    [Fact]
    public async Task Resolver_ReturnsTheAssignedModel_WhenItIsActive()
    {
        await Assign(_activeId);

        await using var db = NewDb();
        Assert.Equal("haiku", (await new ModelSlotResolver(db).FindAsync(ModelSlot.Judge))?.ModelId);
    }

    // Model của suất bị tắt ở màn AI Models ⇒ suất lùi về model của agent gọi, không gọi một model mà quản trị
    // viên đã chủ ý ngưng dùng.
    [Fact]
    public async Task Resolver_ReturnsNull_WhenTheAssignedModelWasDeactivated()
    {
        await Assign(_inactiveId);

        await using var db = NewDb();
        Assert.Null(await new ModelSlotResolver(db).FindAsync(ModelSlot.Judge));
    }

    [Fact]
    public async Task DeletingTheAssignedModel_RemovesTheAssignment_InsteadOfBlockingTheDelete()
    {
        await Assign(_activeId);

        await using (var db = NewDb())
        {
            db.AiModels.Remove(await db.AiModels.SingleAsync(x => x.Id == _activeId));
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
            Assert.Empty(await db.ModelSlotAssignments.ToListAsync());
    }

    [Fact]
    public async Task Update_AssignsThenReassigns_KeepingOneRowPerSlot()
    {
        Assert.Equal(UpdateModelSlotResult.Saved, await Update(ModelSlot.Judge, _activeId, "alice"));
        Assert.Equal(UpdateModelSlotResult.Saved, await Update(ModelSlot.Judge, _otherId, "bob"));

        await using var db = NewDb();
        var row = await db.ModelSlotAssignments.SingleAsync();
        Assert.Equal(ModelSlot.Judge, row.Slot);
        Assert.Equal(_otherId, row.AiModelId);
        Assert.Equal("bob", row.UpdatedByUsername);
    }

    // Bỏ gán = xoá dòng: "chưa khai" chỉ có một cách biểu diễn.
    [Fact]
    public async Task Update_WithNoModel_ClearsTheSlot()
    {
        await Update(ModelSlot.Judge, _activeId, "alice");

        Assert.Equal(UpdateModelSlotResult.Saved, await Update(ModelSlot.Judge, null, "alice"));

        await using var db = NewDb();
        Assert.Empty(await db.ModelSlotAssignments.ToListAsync());
    }

    // Model không tồn tại hoặc đang tắt thì từ chối: lưu được thì resolver vẫn lặng lẽ trả model của agent, và
    // người chọn tưởng mình đang thử model mới.
    [Fact]
    public async Task Update_RejectsAMissingOrInactiveModel_AndLeavesTheSlotAsItWas()
    {
        await Update(ModelSlot.Judge, _activeId, "alice");

        Assert.Equal(UpdateModelSlotResult.ModelUnavailable, await Update(ModelSlot.Judge, _inactiveId, "bob"));
        Assert.Equal(UpdateModelSlotResult.ModelUnavailable, await Update(ModelSlot.Judge, Guid.NewGuid(), "bob"));

        await using var db = NewDb();
        Assert.Equal(_activeId, (await db.ModelSlotAssignments.SingleAsync()).AiModelId);
    }

    [Fact]
    public async Task Update_RejectsAnUnknownSlot()
    {
        Assert.Equal(UpdateModelSlotResult.UnknownSlot, await Update((ModelSlot)999, _activeId, "alice"));
    }

    // Màn AI Models liệt kê MỌI suất, kể cả suất chưa khai; model đang gán mà đã tắt vẫn có trong danh sách chọn
    // (để dropdown hiện đúng giá trị đang lưu) và được đánh dấu để UI nói ra rằng suất đang lùi về model agent.
    [Fact]
    public async Task Query_ListsEverySlot_AndKeepsAnInactiveAssignedModelVisible()
    {
        await Assign(_inactiveId);

        await using var db = NewDb();
        var vm = await new GetModelSlotsQuery(db).ExecuteAsync();

        Assert.Equal(Enum.GetValues<ModelSlot>(), vm.Slots.Select(x => x.Slot));
        var judge = vm.Slots.Single(x => x.Slot == ModelSlot.Judge);
        Assert.Equal(_inactiveId, judge.AiModelId);
        Assert.True(judge.AssignedModelInactive);
        Assert.Equal(new[] { "haiku", "old", "sonnet" }, vm.Models.Select(x => x.ModelId));
    }

    [Fact]
    public async Task Query_LeavesAnUnconfiguredSlotEmpty_AndOffersOnlyActiveModels()
    {
        await using var db = NewDb();
        var vm = await new GetModelSlotsQuery(db).ExecuteAsync();

        var judge = vm.Slots.Single(x => x.Slot == ModelSlot.Judge);
        Assert.Null(judge.AiModelId);
        Assert.False(judge.AssignedModelInactive);
        Assert.Equal(new[] { "haiku", "sonnet" }, vm.Models.Select(x => x.ModelId));
    }

    // Gán thẳng vào DB (bỏ qua use case) để dựng được cả ca model đã tắt.
    private async Task Assign(Guid modelId)
    {
        await using var db = NewDb();
        db.ModelSlotAssignments.Add(new ModelSlotAssignment { Slot = ModelSlot.Judge, AiModelId = modelId });
        await db.SaveChangesAsync();
    }

    private async Task<UpdateModelSlotResult> Update(ModelSlot slot, Guid? modelId, string user)
    {
        await using var db = NewDb();
        return await new UpdateModelSlotUseCase(db, new NullAuditLogger()).ExecuteAsync(slot, modelId, user);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class NullAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditCategory category, AuditAction action, string entityId, string summary,
            object? before = null, object? after = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
