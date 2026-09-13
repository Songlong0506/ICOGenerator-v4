using Microsoft.Playwright;

namespace ICOGenerator.Services.Browser;

/// <summary>
/// Trình duyệt của WebPilot: một <see cref="IBrowserContext"/> PERSISTENT dùng chung cho cả process.
///
/// <para>
/// Vì sao persistent chứ không phải <c>NewContextAsync</c> như tầng kiểm POC: kịch bản đích của
/// WebPilot gồm cả các trang NỘI BỘ có đăng nhập. Context tạm mất sạch cookie sau mỗi lượt, nghĩa là
/// mỗi lần giao việc lại phải đăng nhập lại — thứ agent không làm thay được. Hồ sơ trên đĩa
/// (<c>UserDataDir</c>) giữ phiên đăng nhập nên người dùng đăng nhập MỘT lần bằng tay, các lượt sau
/// agent đi thẳng vào việc.
/// </para>
///
/// <para>
/// Một lượt chạy = một <see cref="IPage"/>, và <see cref="SemaphoreSlim"/> chỉ cho ĐÚNG MỘT lượt chạy
/// tại một thời điểm: đây là trang thử nghiệm một người dùng, còn hai agent cùng lái một hồ sơ
/// Chromium thì thao tác của lượt này đè lên trang của lượt kia mà không lượt nào biết.
/// </para>
///
/// <para>FAIL-OPEN: không có Chromium ⇒ <see cref="AcquirePageAsync"/> trả null kèm <see cref="LastFailure"/>.</para>
/// </summary>
public sealed class WebPilotBrowser : IAsyncDisposable
{
    private readonly WebPilotSettings _settings;
    private readonly IConfiguration _configuration;
    private readonly PlaywrightLauncher _launcher;
    private readonly ILogger<WebPilotBrowser> _logger;

    private readonly SemaphoreSlim _contextLock = new(1, 1);
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private IBrowserContext? _context;

    public WebPilotBrowser(
        WebPilotSettings settings,
        IConfiguration configuration,
        PlaywrightLauncher launcher,
        ILogger<WebPilotBrowser> logger)
    {
        _settings = settings;
        _configuration = configuration;
        _launcher = launcher;
        _logger = logger;
    }

    /// <summary>Lý do không lái được browser, để hiện thẳng cho người dùng thay vì một lỗi trống.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Thư mục hồ sơ Chromium đang dùng — màn hình WebPilot chỉ chỗ cho người dùng đăng nhập tay.</summary>
    public string ProfileDirectory => ResolveUserDataDir();

    /// <summary>
    /// Giành quyền lái browser cho một lượt chạy và mở một trang mới. Trả null khi không có Chromium
    /// (lý do ở <see cref="LastFailure"/>). Người gọi PHẢI dispose giá trị trả về để nhả khoá.
    /// </summary>
    public async Task<WebPilotSession?> AcquirePageAsync(CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            var context = await GetContextAsync();
            if (context == null)
                return null;

            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(Math.Max(5, _settings.NavigationTimeoutSeconds) * 1000);
            return new WebPilotSession(page, _runLock);
        }
        catch
        {
            _runLock.Release();
            throw;
        }
    }

    private async Task<IBrowserContext?> GetContextAsync()
    {
        if (_context != null)
            return _context;

        await _contextLock.WaitAsync();
        try
        {
            if (_context != null)
                return _context;

            var executablePath = string.IsNullOrWhiteSpace(_settings.BrowserPath) ? null : _settings.BrowserPath;
            var options = new BrowserTypeLaunchPersistentContextOptions { Headless = _settings.Headless };
            if (executablePath != null)
                options.ExecutablePath = executablePath;

            var userDataDir = ResolveUserDataDir();
            Directory.CreateDirectory(userDataDir);

            _context = await _launcher.LaunchAsync(
                pw => pw.Chromium.LaunchPersistentContextAsync(userDataDir, options),
                executablePath,
                _settings.AutoInstall);

            if (_context == null)
            {
                LastFailure = _launcher.LastFailure ?? "không rõ lý do";
                _logger.LogWarning("WebPilot không khởi động được Chromium: {Reason}", LastFailure);
            }
            else
            {
                LastFailure = null;
                _logger.LogInformation("WebPilot dùng hồ sơ Chromium tại {Dir} (headless={Headless}).", userDataDir, _settings.Headless);
            }

            return _context;
        }
        finally
        {
            _contextLock.Release();
        }
    }

    /// <summary>
    /// Hồ sơ nằm CẠNH workspace chứ không nằm trong repo: nó là dữ liệu máy (cookie đăng nhập thật),
    /// không phải sản phẩm của dự án nào.
    /// </summary>
    private string ResolveUserDataDir()
    {
        if (!string.IsNullOrWhiteSpace(_settings.UserDataDir))
            return _settings.UserDataDir;

        var root = _configuration["AgentWorkspace:RootPath"];
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Path.GetTempPath(), "webpilot-profile")
            : Path.Combine(root, ".webpilot-profile");
    }

    public async ValueTask DisposeAsync()
    {
        if (_context != null)
            await _context.DisposeAsync();
        _contextLock.Dispose();
        _runLock.Dispose();
    }
}

/// <summary>Một lượt lái browser: giữ trang và giữ khoá "chỉ một lượt chạy" cho tới khi dispose.</summary>
public sealed class WebPilotSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _runLock;
    private bool _released;

    internal WebPilotSession(IPage page, SemaphoreSlim runLock)
    {
        Page = page;
        _runLock = runLock;
    }

    public IPage Page { get; }

    public async ValueTask DisposeAsync()
    {
        if (_released)
            return;
        _released = true;

        try
        {
            await Page.CloseAsync();
        }
        catch
        {
            // Trang đã chết cùng browser: không có gì để dọn, và nhả khoá quan trọng hơn.
        }

        _runLock.Release();
    }
}
