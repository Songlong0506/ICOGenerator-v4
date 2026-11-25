using Xunit;
using ICOGenerator.Services.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ICOGenerator.Tests.Diagnostics;

// Runner của nút "Test Environment": một nhóm hỏng/kẹt không được làm mất kết quả của các nhóm khác, và thứ
// tự hiển thị phải là thứ tự đăng ký (bảng ổn định giữa các lần bấm).
public class EnvironmentDiagnosticsTests
{
    [Fact]
    public async Task RunAsync_KeepsRegistrationOrder_AndTurnsCrashAndTimeoutIntoFailRows()
    {
        var services = new ServiceCollection();
        services.AddScoped<IEnvironmentCheck>(_ => new FakeCheck("Slow-but-ok", TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5)));
        services.AddScoped<IEnvironmentCheck>(_ => new FakeCheck("Crash", throws: true));
        services.AddScoped<IEnvironmentCheck>(_ => new FakeCheck("Stuck", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100)));
        services.AddScoped<IEnvironmentCheck>(_ => new FakeCheck("Fast"));
        using var provider = services.BuildServiceProvider();

        var runner = new EnvironmentDiagnostics(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EnvironmentDiagnostics>.Instance);
        var results = await runner.RunAsync(new EnvironmentCheckContext(true));

        Assert.Equal(new[] { "Slow-but-ok", "Crash", "Stuck", "Fast" }, results.Select(r => r.Group));
        Assert.Equal(EnvironmentCheckStatus.Pass, results[0].Status);
        Assert.Equal(EnvironmentCheckStatus.Fail, results[1].Status);
        Assert.Equal("boom", results[1].Detail);
        Assert.Equal(EnvironmentCheckStatus.Fail, results[2].Status);
        Assert.Equal(EnvironmentCheckStatus.Pass, results[3].Status);
    }

    private sealed class FakeCheck(string group, TimeSpan? delay = null, TimeSpan? timeout = null, bool throws = false) : IEnvironmentCheck
    {
        public string Group => group;
        public TimeSpan Timeout => timeout ?? TimeSpan.FromSeconds(5);

        public async Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(EnvironmentCheckContext context, CancellationToken cancellationToken)
        {
            if (delay is { } d)
                await Task.Delay(d, CancellationToken.None); // cố ý không nghe token: runner vẫn phải cắt được
            if (throws)
                throw new InvalidOperationException("boom");
            return [new EnvironmentCheckResult(group, group, EnvironmentCheckStatus.Pass, "ok")];
        }
    }
}
