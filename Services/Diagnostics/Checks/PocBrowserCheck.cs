using ICOGenerator.Localization;
using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Services.Diagnostics.Checks;

/// <summary>
/// Chromium headless cho tầng kiểm POC. Hỏi đúng browser mà audit POC dùng (<see cref="IPocBrowserProbe"/>) —
/// nên lần bấm đầu trên máy chưa có binary có thể mất ~1 phút vì app tự tải (giống vòng audit POC đầu tiên).
/// <para>
/// Trên IIS: cache browser nằm trong hồ sơ người dùng của application pool. Pool tắt "Load User Profile" thì
/// LOCALAPPDATA trỏ về <c>systemprofile</c> — tải về vẫn được nhưng dễ vướng quyền; đặt
/// <c>PLAYWRIGHT_BROWSERS_PATH</c> về một thư mục chung là cách chắc nhất.
/// </para>
/// </summary>
public sealed class PocBrowserCheck : IEnvironmentCheck
{
    private readonly IPocBrowserProbe _probe;
    private readonly IConfiguration _configuration;

    public PocBrowserCheck(IPocBrowserProbe probe, IConfiguration configuration)
    {
        _probe = probe;
        _configuration = configuration;
    }

    public string Group => Loc.T("EnvCheck.Browser.Group");

    // Rộng hơn trần tự tải (Poc:RuntimeCheck:AutoInstallTimeoutSeconds, mặc định 300s) để lần bấm đầu trên máy
    // mới không bị cắt ngang đúng lúc đang tải.
    public TimeSpan Timeout =>
        TimeSpan.FromSeconds(Math.Max(30, _configuration.GetValue("Poc:RuntimeCheck:AutoInstallTimeoutSeconds", 300)) + 30);

    public async Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(EnvironmentCheckContext context, CancellationToken cancellationToken)
    {
        var name = Loc.T("EnvCheck.Browser.Chromium");
        if (!_configuration.GetValue("Poc:RuntimeCheck:Enabled", true))
            return [new(Group, name, EnvironmentCheckStatus.Warning, Loc.T("EnvCheck.Browser.Disabled"), Loc.T("EnvCheck.Browser.Disabled.Fix"))];

        var results = new List<EnvironmentCheckResult>();
        var result = await _probe.ProbeAsync();
        // Đọc SAU probe: checker đã chốt thư mục cache (PlaywrightBrowserCache) khi khởi tạo.
        var cache = PlaywrightBrowserCache.Current();
        var cacheFromEnv = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PlaywrightBrowserCache.EnvironmentVariable));
        if (result.Available)
        {
            results.Add(new(Group, name, EnvironmentCheckStatus.Pass,
                Loc.T("EnvCheck.Browser.Ok", result.Version ?? "?", result.ExecutablePath ?? cache!)));
        }
        else
        {
            var installScript = Path.Combine(AppContext.BaseDirectory, "playwright.ps1");
            results.Add(new(Group, name, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Browser.Failed"),
                result.ExecutablePath is not null
                    ? Loc.T("EnvCheck.Browser.Failed.FixPath", result.ExecutablePath)
                    : Loc.T("EnvCheck.Browser.Failed.Fix", installScript, cache!),
                result.Failure));
        }

        // Hồ sơ người dùng không được nạp (IIS: Load User Profile = False) ⇒ LOCALAPPDATA của hệ thống.
        if (!cacheFromEnv && result.ExecutablePath is null
            && cache!.Contains("systemprofile", StringComparison.OrdinalIgnoreCase))
            results.Add(new(Group, Loc.T("EnvCheck.Browser.Cache"), EnvironmentCheckStatus.Warning,
                Loc.T("EnvCheck.Browser.Cache.SystemProfile", cache), Loc.T("EnvCheck.Browser.Cache.Fix")));
        else
            results.Add(new(Group, Loc.T("EnvCheck.Browser.Cache"), EnvironmentCheckStatus.Info, cache!));

        return results;
    }
}
