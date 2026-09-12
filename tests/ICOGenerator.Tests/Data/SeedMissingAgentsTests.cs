using System.ComponentModel;
using System.Reflection;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Data;

// Seed agent TỪNG bị bọc trong "if (!await db.Agents.AnyAsync())", nghĩa là một vai thêm sau này sẽ
// không bao giờ xuất hiện trên DB đã chạy: máy dev với DB mới thấy chạy tốt, còn người dùng mở màn hình
// ra thì "chưa cấu hình agent". Bộ test này giữ hành vi bù-vai-thiếu, và giữ luôn hai bất biến của nó:
// chạy lại không nhân đôi, và KHÔNG đụng tới cấu hình admin đã chỉnh.
public class SeedMissingAgentsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public SeedMissingAgentsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(new AiModel { ModelId = "gpt-5.6-luna", Endpoint = "https://api.openai.com/v1", ApiKey = "" });

        // ToolDefinitions bình thường do ToolDiscoveryService đồng bộ lúc khởi động; ở đây dựng thẳng
        // từ cùng nguồn (reflection trên ToolTypes) để test không phụ thuộc thứ tự khởi tạo.
        foreach (var (type, method) in AllToolMethods())
        {
            db.ToolDefinitions.Add(new ToolDefinition
            {
                Name = method.Name,
                DisplayName = method.Name,
                Description = method.GetCustomAttribute<DescriptionAttribute>()!.Description,
                ServiceType = type.Name,
                MethodName = method.Name,
                IsActive = true
            });
        }

        db.SaveChanges();
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    private static IEnumerable<(Type Type, MethodInfo Method)> AllToolMethods() =>
        ICOGenerator.Services.Tools.Registry.ToolDiscoveryService.ToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public).Select(m => (Type: t, Method: m)))
            .Where(x => x.Method.GetCustomAttribute<DescriptionAttribute>() != null);

    [Fact]
    public async Task SeedsEveryRole_OnAnEmptyDatabase()
    {
        await using var db = NewDb();

        await DbInitializer.SeedMissingAgentsAsync(db);

        var roles = await db.Agents.Select(x => x.RoleKey).ToListAsync();
        Assert.Equal(Enum.GetValues<AgentRoleKey>().Length, roles.Count);
        Assert.Contains(AgentRoleKey.WebPilot, roles);
    }

    [Fact]
    public async Task AddsOnlyTheMissingRole_OnADatabaseThatPredatesIt()
    {
        // Đúng tình huống của DB đang chạy: đã có 5 vai cũ, chưa có WebPilot.
        await using (var seed = NewDb())
        {
            var modelId = seed.AiModels.Select(x => x.Id).First();
            foreach (var role in new[] { AgentRoleKey.BusinessAnalyst, AgentRoleKey.TechLead, AgentRoleKey.Developer, AgentRoleKey.Tester, AgentRoleKey.UiUx })
                seed.Agents.Add(new Agent { RoleKey = role, AiModelId = modelId });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        await DbInitializer.SeedMissingAgentsAsync(db);

        var webPilot = await db.Agents.SingleAsync(x => x.RoleKey == AgentRoleKey.WebPilot);
        var toolNames = await db.AgentTools
            .Where(x => x.AgentId == webPilot.Id)
            .Select(x => x.ToolDefinition.Name)
            .ToListAsync();

        Assert.Equal(6, await db.Agents.CountAsync());
        Assert.Contains("OpenUrl", toolNames);
        Assert.Contains("ClickControl", toolNames);
    }

    [Fact]
    public async Task RunningTwice_DoesNotDuplicateAnything()
    {
        await using var db = NewDb();

        await DbInitializer.SeedMissingAgentsAsync(db);
        var agentsAfterFirst = await db.Agents.CountAsync();
        var toolsAfterFirst = await db.AgentTools.CountAsync();

        await DbInitializer.SeedMissingAgentsAsync(db);

        Assert.Equal(agentsAfterFirst, await db.Agents.CountAsync());
        Assert.Equal(toolsAfterFirst, await db.AgentTools.CountAsync());
    }

    [Fact]
    public async Task DoesNotRestoreToolsAnAdminRemoved()
    {
        // Admin gỡ tick một tool là quyết định của họ; seed lại mỗi lần khởi động sẽ âm thầm hoàn tác.
        await using var db = NewDb();
        await DbInitializer.SeedMissingAgentsAsync(db);

        var developer = await db.Agents.SingleAsync(x => x.RoleKey == AgentRoleKey.Developer);
        var removed = await db.AgentTools
            .Include(x => x.ToolDefinition)
            .FirstAsync(x => x.AgentId == developer.Id && x.ToolDefinition.Name == "RunCommand");
        db.AgentTools.Remove(removed);
        await db.SaveChangesAsync();

        await DbInitializer.SeedMissingAgentsAsync(db);

        Assert.False(await db.AgentTools
            .Include(x => x.ToolDefinition)
            .AnyAsync(x => x.AgentId == developer.Id && x.ToolDefinition.Name == "RunCommand"));
    }

    [Fact]
    public async Task DoesNothing_WhenNoModelIsConfiguredYet()
    {
        // Agent.AiModelId không nullable — seed khi chưa có model nào sẽ dựng một agent trỏ vào hư không.
        await using var db = NewDb();
        db.AiModels.RemoveRange(db.AiModels);
        await db.SaveChangesAsync();

        await DbInitializer.SeedMissingAgentsAsync(db);

        Assert.Equal(0, await db.Agents.CountAsync());
    }

    public void Dispose() => _connection.Dispose();
}
