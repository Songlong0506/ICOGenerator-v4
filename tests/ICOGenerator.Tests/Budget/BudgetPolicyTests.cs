using ICOGenerator.Services.Budget;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICOGenerator.Tests.Budget;

// Locks the config-bound USD caps + accounting-window math the budget circuit breaker reads.
public class BudgetPolicyTests
{
    [Fact]
    public void HasAnyLimit_TrueOnlyWhenEnabledAndSomeLimitSet()
    {
        Assert.True(new BudgetPolicy(enabled: true, BudgetPeriod.Monthly, systemUsdLimit: 50m, perProjectUsdLimit: 0m).HasAnyLimit);
        Assert.True(new BudgetPolicy(enabled: true, BudgetPeriod.Monthly, systemUsdLimit: 0m, perProjectUsdLimit: 10m).HasAnyLimit);
        // Opt-in: both caps 0 → nothing to enforce (guard skips the DB entirely).
        Assert.False(new BudgetPolicy(enabled: true, BudgetPeriod.Monthly, systemUsdLimit: 0m, perProjectUsdLimit: 0m).HasAnyLimit);
        // Master switch off → no enforcement even with caps set.
        Assert.False(new BudgetPolicy(enabled: false, BudgetPeriod.Monthly, systemUsdLimit: 50m, perProjectUsdLimit: 10m).HasAnyLimit);
    }

    [Fact]
    public void NegativeLimits_AreTreatedAsNoLimit()
    {
        var policy = new BudgetPolicy(enabled: true, BudgetPeriod.Monthly, systemUsdLimit: -5m, perProjectUsdLimit: -1m);

        Assert.Equal(0m, policy.SystemUsdLimit);
        Assert.Equal(0m, policy.PerProjectUsdLimit);
        Assert.False(policy.HasAnyLimit);
    }

    [Fact]
    public void WindowStart_Daily_IsMidnightUtcToday()
    {
        var policy = new BudgetPolicy(enabled: true, BudgetPeriod.Daily, 1m, 0m);
        var now = new DateTime(2026, 6, 24, 13, 45, 7, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 6, 24, 0, 0, 0, DateTimeKind.Utc), policy.WindowStart(now));
    }

    [Fact]
    public void WindowStart_Monthly_IsFirstOfMonthUtc()
    {
        var policy = new BudgetPolicy(enabled: true, BudgetPeriod.Monthly, 1m, 0m);
        var now = new DateTime(2026, 6, 24, 13, 45, 7, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), policy.WindowStart(now));
    }

    [Fact]
    public void WindowStart_Total_IsMinValue_SoAllHistoryCounts()
    {
        var policy = new BudgetPolicy(enabled: true, BudgetPeriod.Total, 1m, 0m);

        Assert.Equal(DateTime.MinValue, policy.WindowStart(DateTime.UtcNow));
    }

    [Fact]
    public void ConfigConstructor_ParsesEnumAndDecimals()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Budget:Enabled"] = "true",
                ["Budget:Period"] = "Daily",
                ["Budget:SystemUsdLimit"] = "50.5",
                ["Budget:PerProjectUsdLimit"] = "10"
            })
            .Build();

        var policy = new BudgetPolicy(config);

        Assert.True(policy.Enabled);
        Assert.Equal(BudgetPeriod.Daily, policy.Period);
        Assert.Equal(50.5m, policy.SystemUsdLimit);
        Assert.Equal(10m, policy.PerProjectUsdLimit);
    }

    [Fact]
    public void ConfigConstructor_DefaultsToMonthly_OptInOff()
    {
        // Empty config → defaults: enabled but no caps, so nothing is enforced until an admin sets a number.
        var policy = new BudgetPolicy(new ConfigurationBuilder().Build());

        Assert.Equal(BudgetPeriod.Monthly, policy.Period);
        Assert.Equal(0m, policy.SystemUsdLimit);
        Assert.Equal(0m, policy.PerProjectUsdLimit);
        Assert.False(policy.HasAnyLimit);
    }
}
