using ICOGenerator.Application.Agents;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
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

    // The ApiKey value-converter needs an IApiKeyProtector; encryption is irrelevant to these tests.
    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
