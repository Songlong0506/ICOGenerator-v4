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
        if (!GitRemoteUrl.TryParse(remoteUrl, out var host, out var path))
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
}
