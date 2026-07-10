using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Tools.PullRequests;

/// <summary>
/// Tạo Pull Request thật trên GitHub qua REST API (<c>POST /repos/{owner}/{repo}/pulls</c>) bằng token
/// cấu hình ở <c>PullRequest:GitHubToken</c> (nạp qua biến môi trường <c>PullRequest__GitHubToken</c> —
/// KHÔNG commit). Chỉ xử lý remote github.com; mọi trường hợp khác (thiếu token, remote khác, API lỗi)
/// trả <c>Created = false</c> kèm lý do để <see cref="GitTools.OpenPullRequest"/> fallback về link compare.
/// </summary>
public class GitHubPullRequestPublisher : IPullRequestPublisher
{
    // GitHub owner/repo: chữ–số–'.'–'_'–'-'. Chốt chặn để owner/repo (lấy từ remote URL) không chèn
    // được ký tự lạ vào đường dẫn API.
    private static readonly Regex SafeSegment = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;

    public GitHubPullRequestPublisher(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _configuration = configuration;
    }

    public async Task<PullRequestPublishResult> PublishAsync(
        string? remoteUrl, string baseBranch, string headBranch, string title, string body,
        CancellationToken cancellationToken = default)
    {
        var token = _configuration["PullRequest:GitHubToken"];
        if (string.IsNullOrWhiteSpace(token))
            return new(false, null, "chưa cấu hình PullRequest:GitHubToken");

        if (!TryGetRepo(remoteUrl, out var owner, out var repo))
            return new(false, null, "remote không phải GitHub (github.com)");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"repos/{owner}/{repo}/pulls")
            {
                Content = JsonContent.Create(new
                {
                    title,
                    head = headBranch,
                    @base = baseBranch,
                    body
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.Created)
                return new(true, ExtractHtmlUrl(json), "đã tạo PR trên GitHub");

            // 422 thường là PR cho head→base đã tồn tại, hoặc head/base không hợp lệ.
            return new(false, null, $"GitHub API trả {(int)response.StatusCode}: {Truncate(json, 300)}");
        }
        catch (Exception ex)
        {
            return new(false, null, $"lỗi gọi GitHub API: {ex.Message}");
        }
    }

    // Lấy owner/repo từ remote URL nếu là github.com. Hàm thuần — unit-test trực tiếp.
    public static bool TryGetRepo(string? remoteUrl, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        if (!GitRemoteUrl.TryParse(remoteUrl, out var host, out var path))
            return false;
        if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !SafeSegment.IsMatch(parts[0]) || !SafeSegment.IsMatch(parts[1]))
            return false;

        owner = parts[0];
        repo = parts[1];
        return true;
    }

    // Đọc "html_url" (link PR cho người xem) từ JSON GitHub trả về. Hàm thuần — unit-test trực tiếp.
    public static string? ExtractHtmlUrl(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("html_url", out var url) ? url.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "…";
}
