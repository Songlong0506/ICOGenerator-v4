using ICOGenerator.Application.Agents;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Tools.Registry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Agents;

// Cập nhật agent: gán tool theo danh sách chọn — chỉ tool còn tồn tại VÀ đang active được thêm,
// tool bỏ chọn bị gỡ. Chạy trên AppDbContext thật (Sqlite in-memory).
public class UpdateAgentUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public UpdateAgentUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ExecuteAsync_AddsOnlyActiveExistingTools_AndRemovesDeselected()
    {
        var agentId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var keptTool = Guid.NewGuid();      // đã gán, vẫn được chọn
        var removedTool = Guid.NewGuid();   // đã gán, bị bỏ chọn
        var newActiveTool = Guid.NewGuid(); // chưa gán, active → được thêm
        var inactiveTool = Guid.NewGuid();  // chưa gán, inactive → bị bỏ qua
        var missingTool = Guid.NewGuid();   // id không tồn tại → bị bỏ qua

        await using (var db = NewDb())
        {
            db.AiModels.Add(new AiModel { Id = modelId, ModelId = "m", Endpoint = "http://x", ApiKey = "k" });
            // (ServiceType, MethodName) có unique index nên phải khác nhau giữa các tool.
            db.ToolDefinitions.AddRange(
                new ToolDefinition { Id = keptTool, Name = "Kept", ServiceType = "T", MethodName = "Kept", IsActive = true },
                new ToolDefinition { Id = removedTool, Name = "Removed", ServiceType = "T", MethodName = "Removed", IsActive = true },
                new ToolDefinition { Id = newActiveTool, Name = "NewActive", ServiceType = "T", MethodName = "NewActive", IsActive = true },
                new ToolDefinition { Id = inactiveTool, Name = "Inactive", ServiceType = "T", MethodName = "Inactive", IsActive = false });
            db.Agents.Add(new Agent { Id = agentId, AiModelId = modelId });
            db.AgentTools.AddRange(
                new AgentTool { AgentId = agentId, ToolDefinitionId = keptTool },
                new AgentTool { AgentId = agentId, ToolDefinitionId = removedTool });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var result = await new UpdateAgentUseCase(db, new NullAuditLogger()).ExecuteAsync(new AgentEditVm
            {
                Id = agentId,
                AiModelId = modelId,
                ToolDefinitionIds = [keptTool, newActiveTool, inactiveTool, missingTool]
            });
            Assert.Equal(UpdateAgentResult.Success, result);
        }

        await using (var db = NewDb())
        {
            var toolIds = await db.AgentTools
                .Where(x => x.AgentId == agentId)
                .Select(x => x.ToolDefinitionId)
                .ToListAsync();
            Assert.Equal(new[] { keptTool, newActiveTool }.OrderBy(x => x), toolIds.OrderBy(x => x));
        }
    }

    // Nhóm tool cấp-cả-gói (WebTools, xem ToolGroupAllOrNothingAttribute): màn hình chỉ gửi lên id của
    // MỘT tool đại diện, nên chốt nở-ra-cả-nhóm phải nằm ở use case. Một form POST gửi tay hay một tab
    // còn mở bản giao diện cũ cũng gửi lên tập con — và cấu hình nửa vời thì agent không lái nổi trình
    // duyệt mà chẳng có gì báo lỗi.
    [Fact]
    public async Task ExecuteAsync_GrantsTheWholeGroup_WhenOnlyOneMemberWasSent()
    {
        var lockedGroup = ToolDiscoveryService.AllOrNothingGroups[0].ServiceType;
        var (agentId, modelId, groupToolIds) = await SeedLockedGroupAsync(lockedGroup);

        await using (var db = NewDb())
        {
            var result = await new UpdateAgentUseCase(db, new NullAuditLogger()).ExecuteAsync(new AgentEditVm
            {
                Id = agentId,
                AiModelId = modelId,
                ToolDefinitionIds = [groupToolIds[0]]
            });
            Assert.Equal(UpdateAgentResult.Success, result);
        }

        await using (var check = NewDb())
        {
            var stored = await check.AgentTools.Where(x => x.AgentId == agentId)
                .Select(x => x.ToolDefinitionId).ToListAsync();
            Assert.Equal(groupToolIds.OrderBy(x => x), stored.OrderBy(x => x));
        }
    }

    // Chiều ngược lại phải giữ: khoá nhóm là bỏ đi một độ chi tiết không có thật, KHÔNG phải biến nhóm
    // thành thứ đã bật thì không gỡ được.
    [Fact]
    public async Task ExecuteAsync_RevokesTheWholeGroup_WhenNoMemberWasSent()
    {
        var lockedGroup = ToolDiscoveryService.AllOrNothingGroups[0].ServiceType;
        var (agentId, modelId, groupToolIds) = await SeedLockedGroupAsync(lockedGroup, grantAll: true);

        await using (var db = NewDb())
        {
            var result = await new UpdateAgentUseCase(db, new NullAuditLogger()).ExecuteAsync(new AgentEditVm
            {
                Id = agentId,
                AiModelId = modelId,
                ToolDefinitionIds = []
            });
            Assert.Equal(UpdateAgentResult.Success, result);
        }

        await using (var check = NewDb())
        {
            Assert.Empty(await check.AgentTools.Where(x => x.AgentId == agentId).ToListAsync());
        }
    }

    private async Task<(Guid AgentId, Guid ModelId, List<Guid> GroupToolIds)> SeedLockedGroupAsync(
        string serviceType, bool grantAll = false)
    {
        var agentId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var groupToolIds = new List<Guid>();

        await using var db = NewDb();
        db.AiModels.Add(new AiModel { Id = modelId, ModelId = "m", Endpoint = "http://x", ApiKey = "k" });
        db.Agents.Add(new Agent { Id = agentId, AiModelId = modelId });

        foreach (var method in new[] { "A", "B", "C" })
        {
            var id = Guid.NewGuid();
            groupToolIds.Add(id);
            db.ToolDefinitions.Add(new ToolDefinition
            {
                Id = id, Name = method, ServiceType = serviceType, MethodName = method, IsActive = true
            });
            if (grantAll)
                db.AgentTools.Add(new AgentTool { AgentId = agentId, ToolDefinitionId = id });
        }

        await db.SaveChangesAsync();
        return (agentId, modelId, groupToolIds);
    }

    [Fact]
    public async Task ExecuteAsync_RequiresExistingModel()
    {
        var agentId = Guid.NewGuid();
        var modelId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.AiModels.Add(new AiModel { Id = modelId, ModelId = "m", Endpoint = "http://x", ApiKey = "k" });
            db.Agents.Add(new Agent { Id = agentId, AiModelId = modelId });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var result = await new UpdateAgentUseCase(db, new NullAuditLogger()).ExecuteAsync(new AgentEditVm
            {
                Id = agentId,
                AiModelId = Guid.NewGuid() // model không tồn tại
            });
            Assert.Equal(UpdateAgentResult.ModelRequired, result);
        }
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class NullAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditCategory category, AuditAction action, string entityId, string summary,
            object? before = null, object? after = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
