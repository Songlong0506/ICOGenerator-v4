using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

/// <summary>
/// Chọn thư mục cache Playwright: app pool IIS không nạp hồ sơ người dùng thì đường mặc định rỗng/tương đối
/// (<c>ms-playwright</c>) hoặc nằm dưới <c>systemprofile</c> — phải đổi sang thư mục dùng chung của máy để tự tải được.
/// </summary>
public class PlaywrightBrowserCacheTests
{
    private static readonly string Fallback = Path.Combine(Path.GetTempPath(), "shared", "ms-playwright");
    private static readonly string UserDefault = Path.Combine(Path.GetTempPath(), "user", "ms-playwright");

    [Fact]
    public void Usable_default_is_pinned_so_the_driver_uses_the_same_folder()
    {
        var (path, set) = PlaywrightBrowserCache.Resolve(null, null, UserDefault, Fallback);
        Assert.Equal(UserDefault, path);
        Assert.True(set);
    }

    [Theory]
    [InlineData("ms-playwright")]
    [InlineData(@"C:\Windows\system32\config\systemprofile\AppData\Local\ms-playwright")]
    public void Unusable_default_falls_back_to_machine_folder(string userDefault)
    {
        var (path, set) = PlaywrightBrowserCache.Resolve(null, null, userDefault, Fallback);
        Assert.Equal(Fallback, path);
        Assert.True(set);
    }

    [Fact]
    public void Existing_environment_variable_wins_over_defaults()
    {
        var (path, set) = PlaywrightBrowserCache.Resolve(null, "/opt/browsers", "ms-playwright", Fallback);
        Assert.Equal("/opt/browsers", path);
        Assert.False(set);
    }

    [Fact]
    public void Configured_path_wins_and_is_unquoted()
    {
        var configured = Path.Combine(Path.GetTempPath(), "configured");
        var (path, set) = PlaywrightBrowserCache.Resolve($"\"{configured}\"", "/opt/browsers", UserDefault, Fallback);
        Assert.Equal(configured, path);
        Assert.True(set);
    }

    [Fact]
    public void Relative_configured_path_is_ignored()
    {
        var (path, _) = PlaywrightBrowserCache.Resolve("browsers", null, UserDefault, Fallback);
        Assert.Equal(UserDefault, path);
    }
}
