namespace ICOGenerator.Services.Tools;

/// <summary>
/// Suy ra URL "tạo Pull/Merge Request sẵn điền" từ remote URL của repo + nhánh base/head.
/// Hàm thuần (không I/O) nên dễ unit-test; nhận diện GitHub, GitLab, Azure DevOps, Bitbucket
/// từ host. Trả <c>null</c> khi không nhận diện được nhà cung cấp hoặc remote URL rỗng — caller
/// vẫn báo "đã push, hãy mở PR thủ công" thay vì fail.
///
/// Nhánh đã được validate là ref an toàn ở <see cref="GitTools"/> trước khi tới đây; vẫn
/// URL-encode mọi giá trị đưa vào query để chống chèn tham số.
/// </summary>
public static class PullRequestUrlBuilder
{
    public static string? Build(string? remoteUrl, string baseBranch, string headBranch, string? title)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return null;

        if (!TryParse(remoteUrl.Trim(), out var host, out var path))
            return null;

        var lowerHost = host.ToLowerInvariant();
        var baseRepo = $"https://{host}/{path}";
        // Trong query param thì encode; trong PATH (compare của GitHub) thì để literal — nhánh đã là ref
        // an toàn (chỉ [A-Za-z0-9._/-]) nên hợp lệ trong path, và encode dấu '/' thành %2F sẽ làm GitHub
        // không phân giải đúng tên nhánh.
        var head = Uri.EscapeDataString(headBranch);
        var @base = Uri.EscapeDataString(baseBranch);

        if (lowerHost.Contains("github"))
        {
            var url = $"{baseRepo}/compare/{baseBranch}...{headBranch}?expand=1";
            if (!string.IsNullOrWhiteSpace(title))
                url += $"&title={Uri.EscapeDataString(title)}";
            return url;
        }

        if (lowerHost.Contains("gitlab"))
        {
            var url = $"{baseRepo}/-/merge_requests/new"
                      + $"?merge_request%5Bsource_branch%5D={head}&merge_request%5Btarget_branch%5D={@base}";
            if (!string.IsNullOrWhiteSpace(title))
                url += $"&merge_request%5Btitle%5D={Uri.EscapeDataString(title)}";
            return url;
        }

        if (lowerHost.Contains("bitbucket"))
            return $"{baseRepo}/pull-requests/new?source={head}&dest={@base}";

        // Azure DevOps: path đã giữ nguyên đoạn "org/project/_git/repo" (HTTPS), chỉ cần nối hậu tố.
        if (lowerHost.Contains("dev.azure.com") || lowerHost.Contains("visualstudio.com"))
            return $"{baseRepo}/pullrequestcreate?sourceRef={head}&targetRef={@base}";

        return null;
    }

    // Tách remote URL thành (host, path) với path = "owner/repo" (hoặc "org/project/_git/repo" cho Azure),
    // đã bỏ ".git" cuối. Hỗ trợ HTTPS (kèm userinfo@/port) và SSH dạng scp "git@host:owner/repo".
    private static bool TryParse(string remoteUrl, out string host, out string path)
    {
        host = string.Empty;
        path = string.Empty;
        string rest;

        if (!remoteUrl.Contains("://") && remoteUrl.Contains('@') && remoteUrl.Contains(':'))
        {
            // scp-like: git@host:owner/repo(.git)
            var at = remoteUrl.IndexOf('@');
            var colon = remoteUrl.IndexOf(':', at + 1);
            if (colon < 0)
                return false;
            host = remoteUrl[(at + 1)..colon];
            rest = remoteUrl[(colon + 1)..];
        }
        else
        {
            var scheme = remoteUrl.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0)
                return false;
            var afterScheme = remoteUrl[(scheme + 3)..];
            var slash = afterScheme.IndexOf('/');
            if (slash < 0)
                return false;
            var authority = afterScheme[..slash];
            rest = afterScheme[(slash + 1)..];

            var at = authority.IndexOf('@'); // strip userinfo
            if (at >= 0)
                authority = authority[(at + 1)..];
            var colon = authority.IndexOf(':'); // strip port
            if (colon >= 0)
                authority = authority[..colon];
            host = authority;
        }

        rest = rest.Trim('/');
        if (rest.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            rest = rest[..^4];

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(rest))
            return false;

        path = rest;
        return true;
    }
}
