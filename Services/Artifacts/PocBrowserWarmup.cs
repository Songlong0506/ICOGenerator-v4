namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Kiểm và (nếu chưa có) tự tải Chromium headless ngay khi app khởi động, thay vì đợi tới vòng audit POC
/// đầu tiên. Đi qua đúng <see cref="IPocBrowserProbe"/> mà audit dùng, nên cùng luật tự tải
/// (<c>Poc:RuntimeCheck:AutoInstall</c>) và cùng instance browser. Chạy dưới nền, không chặn khởi động;
/// fail-open — tải hỏng thì audit vẫn chạy phần tĩnh như cũ. Tắt bằng <c>Poc:RuntimeCheck:WarmUpOnStartup=false</c>.
/// </summary>
public sealed class PocBrowserWarmup : BackgroundService
{
    // Nhường app khởi động xong (migrate DB, seed) trước khi tải ~150MB.
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(15);

    private readonly IPocBrowserProbe _probe;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PocBrowserWarmup> _logger;

    public PocBrowserWarmup(IPocBrowserProbe probe, IConfiguration configuration, ILogger<PocBrowserWarmup> logger)
    {
        _probe = probe;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Poc:RuntimeCheck:Enabled", true)
            || !_configuration.GetValue("Poc:RuntimeCheck:WarmUpOnStartup", true))
            return;

        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            var result = await _probe.ProbeAsync();
            if (result.Available)
                _logger.LogInformation("Headless Chromium {Version} is ready for POC checks (cache: {Cache}).",
                    result.Version, PlaywrightBrowserCache.Current());
            else
                _logger.LogWarning("Headless Chromium is not available — POC audits only run static checks: {Reason} (cache: {Cache}).",
                    result.Failure, PlaywrightBrowserCache.Current());
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Headless Chromium warm-up failed.");
        }
    }
}
