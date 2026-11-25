using ICOGenerator.Configuration;
using ICOGenerator.Localization;

namespace ICOGenerator.Services.Diagnostics.Checks;

/// <summary>
/// Đăng nhập: chế độ (SSO hay tự đăng nhập <c>LocalRole</c>), HTTPS, và IdentityServer có trả lời không.
/// <para>
/// HTTPS không phải tiểu tiết: ở môi trường khác Development cookie đăng nhập là <c>Secure</c>-only, chạy
/// site IIS chỉ có binding HTTP thì đăng nhập xong vẫn bị đá về trang Login.
/// </para>
/// </summary>
public sealed class AuthenticationCheck : IEnvironmentCheck
{
    private readonly IdentityServerSettings _settings;
    private readonly IWebHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;

    public AuthenticationCheck(IConfiguration configuration, IWebHostEnvironment environment, IHttpClientFactory httpClientFactory)
    {
        _settings = configuration.GetSection(IdentityServerSettings.SectionName).Get<IdentityServerSettings>() ?? new IdentityServerSettings();
        _environment = environment;
        _httpClientFactory = httpClientFactory;
    }

    public string Group => Loc.T("EnvCheck.Auth.Group");
    public TimeSpan Timeout => TimeSpan.FromSeconds(25);

    public async Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(EnvironmentCheckContext context, CancellationToken cancellationToken)
    {
        var results = new List<EnvironmentCheckResult>();

        results.Add(context.RequestIsHttps || _environment.IsDevelopment()
            ? new(Group, "HTTPS", EnvironmentCheckStatus.Pass, context.RequestIsHttps ? Loc.T("EnvCheck.Auth.Https.Ok") : Loc.T("EnvCheck.Auth.Https.Dev"))
            : new(Group, "HTTPS", EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Auth.Https.Missing"), Loc.T("EnvCheck.Auth.Https.Fix")));

        var mode = Loc.T("EnvCheck.Auth.Mode");
        if (_settings.IsLocalLogin)
        {
            results.Add(new(Group, mode, _environment.IsDevelopment() ? EnvironmentCheckStatus.Info : EnvironmentCheckStatus.Warning,
                Loc.T("EnvCheck.Auth.Local", _settings.LocalRole!.Value),
                _environment.IsDevelopment() ? null : Loc.T("EnvCheck.Auth.Local.Fix")));
            return results;
        }

        results.Add(new(Group, mode, EnvironmentCheckStatus.Info, Loc.T("EnvCheck.Auth.Sso", _settings.BaseURL)));

        var discovery = Loc.T("EnvCheck.Auth.Discovery");
        if (!Uri.TryCreate(_settings.BaseURL.TrimEnd('/') + "/.well-known/openid-configuration", UriKind.Absolute, out var url))
        {
            results.Add(new(Group, discovery, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Auth.BadUrl", _settings.BaseURL),
                Loc.T("EnvCheck.Auth.BadUrl.Fix")));
            return results;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.GetAsync(url, cancellationToken);
            results.Add(response.IsSuccessStatusCode
                ? new(Group, discovery, EnvironmentCheckStatus.Pass, url.ToString())
                : new(Group, discovery, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Auth.Http", (int)response.StatusCode, url),
                    Loc.T("EnvCheck.Auth.Unreachable.Fix")));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            results.Add(new(Group, discovery, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Auth.Unreachable", url),
                Loc.T("EnvCheck.Auth.Unreachable.Fix"), ex.GetBaseException().Message));
        }

        return results;
    }
}
