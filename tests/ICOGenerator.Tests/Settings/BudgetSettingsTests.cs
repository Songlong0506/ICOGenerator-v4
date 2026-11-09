using System.Text.Json.Nodes;
using ICOGenerator.Application.Settings;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Settings;
using ICOGenerator.Tests.Prompts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ICOGenerator.Tests.Settings;

// Chốt mục "LLM Budget" của trang Settings: ghi ra appsettings.json đúng các key mà BudgetPolicy đọc,
// không phụ thuộc culture, và không đụng các key khác của section Budget.
public sealed class BudgetSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "icogen-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "appsettings.json");

    public BudgetSettingsTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, """
        {
          "ConnectionStrings": { "DefaultConnection": "Server=x" },
          "AgentWorkspace": { "RootPath": "/tmp/ws" },
          "Budget": { "_note": "giữ nguyên", "Enabled": true, "Period": "Monthly", "SystemUsdLimit": 0, "PerProjectUsdLimit": 0 }
        }
        """);
    }

    [Fact]
    public async Task Update_WritesKeysThatBudgetPolicyReads_AndKeepsNote()
    {
        var (get, update) = CreateUseCases();
        var input = await get.ExecuteAsync();
        input.BudgetEnabled = true;
        input.BudgetPeriod = BudgetPeriod.Daily;
        input.BudgetSystemUsdLimit = "50.25";
        input.BudgetPerProjectUsdLimit = "10,5"; // dấu phẩy thập phân vẫn phải hiểu đúng

        Assert.Null(await update.ExecuteAsync(input));

        var policy = new BudgetPolicy(new ConfigurationBuilder().AddJsonFile(FilePath).Build());
        Assert.Equal(BudgetPeriod.Daily, policy.Period);
        Assert.Equal(50.25m, policy.SystemUsdLimit);
        Assert.Equal(10.5m, policy.PerProjectUsdLimit);
        Assert.True(policy.HasAnyLimit);

        var budget = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!["Budget"]!;
        Assert.Equal("giữ nguyên", budget["_note"]!.GetValue<string>());

        // Đọc lại cho form: chuỗi invariant, không phụ thuộc culture server.
        var reloaded = await get.ExecuteAsync();
        Assert.Equal("50.25", reloaded.BudgetSystemUsdLimit);
        Assert.Equal("10.5", reloaded.BudgetPerProjectUsdLimit);
        Assert.Equal(BudgetPeriod.Daily, reloaded.BudgetPeriod);
    }

    [Fact]
    public async Task Update_Disabled_TurnsGuardOff()
    {
        var (get, update) = CreateUseCases();
        var input = await get.ExecuteAsync();
        input.BudgetEnabled = false;
        input.BudgetSystemUsdLimit = "100";

        Assert.Null(await update.ExecuteAsync(input));

        var policy = new BudgetPolicy(new ConfigurationBuilder().AddJsonFile(FilePath).Build());
        Assert.False(policy.Enabled);
        Assert.False(policy.HasAnyLimit);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("abc")]
    public async Task Update_RejectsInvalidLimit_AndLeavesFileUntouched(string limit)
    {
        var (get, update) = CreateUseCases();
        var before = await File.ReadAllTextAsync(FilePath);
        var input = await get.ExecuteAsync();
        input.BudgetSystemUsdLimit = limit;

        Assert.NotNull(await update.ExecuteAsync(input));
        Assert.Equal(before, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task Get_MissingBudgetSection_FallsBackToPolicyDefaults()
    {
        File.WriteAllText(FilePath, """{ "ConnectionStrings": { "DefaultConnection": "x" } }""");
        var (get, _) = CreateUseCases();

        var vm = await get.ExecuteAsync();

        Assert.True(vm.BudgetEnabled);
        Assert.Equal(BudgetPeriod.Monthly, vm.BudgetPeriod);
        Assert.Equal("0", vm.BudgetSystemUsdLimit);
        Assert.Equal("0", vm.BudgetPerProjectUsdLimit);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("  ", 0)]
    [InlineData("12", 12)]
    [InlineData("12.5", 12.5)]
    [InlineData("12,5", 12.5)]
    [InlineData("$ 7", 7)]
    public void TryParseUsd_AcceptsCultureFreeInput(string value, double expected)
    {
        Assert.True(UpdateAppSettingsUseCase.TryParseUsd(value, out var result));
        Assert.Equal((decimal)expected, result);
    }

    private (GetAppSettingsQuery, UpdateAppSettingsUseCase) CreateUseCases()
    {
        var store = new AppSettingsFileStore(new FakeHostEnvironment { ContentRootPath = _root });
        var get = new GetAppSettingsQuery(store);
        return (get, new UpdateAppSettingsUseCase(store, get, new SavePromptVersionUseCaseTests.NullAuditLogger()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
