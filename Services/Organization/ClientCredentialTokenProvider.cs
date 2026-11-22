using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ICOGenerator.Configuration;

namespace ICOGenerator.Services.Organization;

/// <summary>
/// Lấy access token OAuth2 <c>client_credentials</c> cho employee-api và giữ lại tới gần lúc hết hạn.
/// Singleton: một lần đồng bộ gọi API hàng trăm lần (mỗi người nghỉ việc một lời gọi leaving-info), xin
/// token mới cho từng lời gọi là dội IdentityServer vô ích.
/// </summary>
public sealed class ClientCredentialTokenProvider
{
    /// <summary>Tên named-HttpClient gọi token endpoint (đăng ký ở ApplicationServiceCollectionExtensions).</summary>
    public const string HttpClientName = "EmployeeApiToken";

    // Xin token mới sớm hơn hạn thật một khoảng, để token không hết hạn giữa chừng một request đang bay.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EmployeeApiClientCredentialSettings _settings;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public ClientCredentialTokenProvider(IHttpClientFactory httpClientFactory, EmployeeApiSettings settings, TimeProvider? time = null)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.ClientCredential;
        _time = time ?? TimeProvider.System;
    }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (IsValid())
            return _accessToken!;

        await _lock.WaitAsync(ct);
        try
        {
            if (IsValid())
                return _accessToken!;

            var token = await RequestTokenAsync(ct);
            _accessToken = token.AccessToken;
            _expiresAt = _time.GetUtcNow().AddSeconds(Math.Max(token.ExpiresIn, 0));
            return _accessToken!;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Bỏ token đang giữ — gọi khi API trả 401 (token bị thu hồi trước hạn).</summary>
    public void Invalidate()
    {
        _accessToken = null;
        _expiresAt = DateTimeOffset.MinValue;
    }

    private bool IsValid()
        => _accessToken is not null && _time.GetUtcNow() < _expiresAt - RefreshMargin;

    private async Task<TokenResponse> RequestTokenAsync(CancellationToken ct)
    {
        const string section = $"{EmployeeApiSettings.SectionName}:ClientCredential";
        if (string.IsNullOrWhiteSpace(_settings.TokenUrl))
            throw new InvalidOperationException($"Thiếu cấu hình {section}:TokenUrl.");
        if (string.IsNullOrWhiteSpace(_settings.ClientId))
            throw new InvalidOperationException($"Thiếu cấu hình {section}:ClientId.");
        if (string.IsNullOrWhiteSpace(_settings.ClientSecret))
            throw new InvalidOperationException(
                $"Thiếu {section}:ClientSecret cho client '{_settings.ClientId}' — nạp qua biến môi trường EmployeeApi__ClientCredential__ClientSecret.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = _settings.ClientSecret,
        };

        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Xin token cho client '{_settings.ClientId}' thất bại. Status: {(int)response.StatusCode}. Response: {body}");
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            throw new InvalidOperationException($"Token endpoint trả về response không hợp lệ cho client '{_settings.ClientId}'.");

        return token;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
