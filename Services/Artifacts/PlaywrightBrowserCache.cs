namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Chọn thư mục cache bộ browser của Playwright (biến môi trường <c>PLAYWRIGHT_BROWSERS_PATH</c>) TRƯỚC khi
/// Playwright khởi động hay tự tải Chromium.
/// <para>
/// Lý do tồn tại: trên IIS, application pool không nạp hồ sơ người dùng thì thư mục mặc định
/// (<c>%LOCALAPPDATA%\ms-playwright</c>) hoặc rỗng — <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// trả chuỗi rỗng khi thư mục không tồn tại, nên đường cache thành <c>ms-playwright</c> tương đối — hoặc rơi
/// vào <c>systemprofile</c> mà tài khoản pool không ghi được. Cả hai trường hợp làm bước tự tải hỏng âm thầm
/// và người vận hành phải cài tay. Ở đây đổi sang thư mục dùng chung của máy (<c>%ProgramData%\ms-playwright</c>)
/// để máy mới vẫn "chỉ cần deploy là chạy".
/// </para>
/// Thứ tự: <c>Poc:RuntimeCheck:BrowsersPath</c> → thư mục <c>ms-playwright</c> ĐÓNG GÓI cạnh app (CI tải sẵn
/// vào gói publish — server khỏi cần ra internet, và bản browser luôn khớp bản package) →
/// <c>PLAYWRIGHT_BROWSERS_PATH</c> có sẵn → mặc định của
/// Playwright nếu dùng được → thư mục dùng chung của máy. Luôn ghi lại vào biến môi trường để .NET và driver
/// Playwright nhìn cùng một thư mục.
/// </summary>
public static class PlaywrightBrowserCache
{
    public const string EnvironmentVariable = "PLAYWRIGHT_BROWSERS_PATH";
    public const string ConfigKey = "Poc:RuntimeCheck:BrowsersPath";
    private const string FolderName = "ms-playwright";

    /// <summary>Đặt <c>PLAYWRIGHT_BROWSERS_PATH</c> cho process khi cần, trả đường cache sẽ dùng.</summary>
    public static string Ensure(IConfiguration configuration)
    {
        var resolution = Resolve(
            configuration[ConfigKey],
            BundledFolder(),
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            UserDefault(),
            MachineFallback());
        if (resolution.SetEnvironment)
            Environment.SetEnvironmentVariable(EnvironmentVariable, resolution.Path);
        return resolution.Path;
    }

    /// <summary>Đường cache hiện hành (không đổi gì) — dùng cho màn Environment Check.</summary>
    public static string Current()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnv) ? UserDefault() : fromEnv;
    }

    /// <summary>Phần quyết định thuần (test được). <c>SetEnvironment</c> = phải ghi đè biến môi trường.</summary>
    internal static (string Path, bool SetEnvironment) Resolve(
        string? configured, string? bundled, string? fromEnvironment, string userDefault, string machineFallback)
    {
        var explicitPath = configured?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(explicitPath) && System.IO.Path.IsPathFullyQualified(explicitPath))
            return (explicitPath, true);

        if (!string.IsNullOrWhiteSpace(bundled))
            return (bundled, true);

        if (!string.IsNullOrWhiteSpace(fromEnvironment) && System.IO.Path.IsPathFullyQualified(fromEnvironment))
            return (fromEnvironment, false);

        // Luôn ghi biến môi trường, kể cả khi dùng mặc định: driver Node của Playwright tự tính mặc định theo
        // cách KHÁC .NET (LOCALAPPDATA/HOME của process, không phải known-folder) — để nó tự tính là có lúc
        // .NET thấy đường tuyệt đối còn driver tải vào thư mục tương đối dưới thư mục làm việc của app.
        return (IsUsable(userDefault) ? userDefault : machineFallback, true);
    }

    /// <summary><c>&lt;thư mục app&gt;/ms-playwright</c> nếu gói deploy có mang theo, ngược lại null.</summary>
    private static string? BundledFolder()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, FolderName);
        return Directory.Exists(path) ? path : null;
    }

    private static bool IsUsable(string path)
        => System.IO.Path.IsPathFullyQualified(path)
           && !path.Contains("systemprofile", StringComparison.OrdinalIgnoreCase);

    private static string UserDefault()
        => OperatingSystem.IsWindows()
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName)
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", FolderName);

    private static string MachineFallback()
    {
        var shared = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return System.IO.Path.Combine(
            System.IO.Path.IsPathFullyQualified(shared) ? shared : System.IO.Path.GetTempPath(), FolderName);
    }
}
