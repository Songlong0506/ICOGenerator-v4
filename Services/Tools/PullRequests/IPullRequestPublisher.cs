namespace ICOGenerator.Services.Tools.PullRequests;

/// <summary>
/// Kết quả của một lần cố gắng tạo Pull Request qua API nhà cung cấp.
/// <see cref="Created"/> = true kèm <see cref="Url"/> khi tạo được PR thật; ngược lại
/// <see cref="Detail"/> giải thích lý do để caller fallback về link "mở PR thủ công".
/// </summary>
public record PullRequestPublishResult(bool Created, string? Url, string Detail);

/// <summary>
/// Tạo Pull Request thật qua API của nhà cung cấp Git (hiện hỗ trợ GitHub). Không ném lỗi:
/// thiếu cấu hình / remote không hỗ trợ / API lỗi đều trả <see cref="PullRequestPublishResult"/>
/// với <c>Created = false</c> để bước tạo PR luôn xuống cấp êm về link compare.
/// </summary>
public interface IPullRequestPublisher
{
    Task<PullRequestPublishResult> PublishAsync(
        string? remoteUrl, string baseBranch, string headBranch, string title, string body,
        CancellationToken cancellationToken = default);
}
