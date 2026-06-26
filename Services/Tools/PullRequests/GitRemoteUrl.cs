namespace ICOGenerator.Services.Tools.PullRequests;

/// <summary>
/// Tách một remote URL của git thành <c>host</c> + <c>path</c> ("owner/repo", hoặc
/// "org/project/_git/repo" cho Azure DevOps), đã bỏ ".git" cuối. Hỗ trợ HTTPS (kèm userinfo@/port)
/// và SSH dạng scp "git@host:owner/repo". Hàm thuần, dùng chung cho <see cref="PullRequestUrlBuilder"/>
/// (suy ra link PR) và <see cref="GitHubPullRequestPublisher"/> (suy ra owner/repo để gọi API).
/// </summary>
public static class GitRemoteUrl
{
    public static bool TryParse(string? remoteUrl, out string host, out string path)
    {
        host = string.Empty;
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(remoteUrl))
            return false;

        remoteUrl = remoteUrl.Trim();
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
