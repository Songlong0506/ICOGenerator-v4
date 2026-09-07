using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.PullRequests;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICOGenerator.Tests.Tools;

// Mọi tool git nhận repoPath và phải CHẶN trước khi chạy git nếu đường dẫn đó không phải một repo trong
// workspace. Đây là chốt thay cho hành vi cũ: mọi lệnh git chạy ở GỐC workspace — nơi không bao giờ có
// repo — nên bước Pull Request hoặc trả "fatal: not a git repository", hoặc (khi RootPath vô tình nằm
// trong một repo khác) commit vào NHẦM repo mà không ai thấy.
//
// Các ca dưới đây dừng ở tầng kiểm tra nên KHÔNG lệnh git nào được chạy.
public class GitToolsRepoScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"icogen-gittools-{Guid.NewGuid():N}");
    private readonly GitTools _git;
    private readonly string _workspacePath;

    public GitToolsRepoScopeTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _root })
            .Build();

        var resolver = new WorkspacePathResolver(config);
        // IPocRuntimeChecker/PocVisualReviewer chỉ được dùng bởi các tool POC; đường đi trong test này
        // (CurrentWorkspacePath + GetSafeFullPath) không chạm tới chúng.
        var workspace = new WorkspaceTools(config, resolver, null!, null!);
        workspace.SetWorkspace("proj-key");
        _workspacePath = workspace.CurrentWorkspacePath;

        _git = new GitTools(new CommandTools(config, workspace), config, new StubPublisher());
    }

    // Model bỏ trống tham số: nói thẳng phải lấy đường dẫn từ danh sách repo trong prompt, thay vì lặng
    // lẽ lùi về gốc workspace như trước.
    [Fact]
    public async Task GitStatus_WithoutRepoPath_AsksForOne()
    {
        Assert.Contains("Missing repoPath", await _git.GitStatus(""));
    }

    // repoPath do model sinh ⇒ phải đi qua đúng chốt chặn path-traversal của các tool file.
    [Theory]
    [InlineData("../../etc")]
    [InlineData("04_Implementation/../../../tmp")]
    public async Task GitStatus_WithPathEscapingWorkspace_IsBlocked(string repoPath)
    {
        var result = await _git.GitStatus(repoPath);

        Assert.Contains("Command blocked for security reason", result);
        Assert.Contains("escapes the workspace", result);
    }

    [Fact]
    public async Task GitStatus_WithMissingDirectory_SaysSo()
    {
        Assert.Contains("does not exist", await _git.GitStatus("04_Implementation/src"));
    }

    // Ca chính: thư mục CÓ THẬT nhưng chưa được dựng thành repo (workspace cũ, hoặc
    // ProjectRepositorySeeder chưa chạy). Thông điệp phải chỉ đúng chỗ tra cứu, không để agent đoán tiếp.
    [Fact]
    public async Task GitStatus_WhenDirectoryIsNotARepository_SaysWhereToLook()
    {
        Directory.CreateDirectory(Path.Combine(_workspacePath, "04_Implementation", "src"));

        var result = await _git.GitStatus("04_Implementation/src");

        Assert.Contains("not a git repository root", result);
        Assert.Contains("listed in the task prompt", result);
    }

    // Chốt phải chạy TRƯỚC mọi thao tác ghi/đẩy: OpenPullRequest trên một thư mục không phải repo không
    // được push gì hết, và cũng không được gọi tới publisher.
    [Fact]
    public async Task OpenPullRequest_WhenDirectoryIsNotARepository_StopsBeforePushing()
    {
        Directory.CreateDirectory(Path.Combine(_workspacePath, "04_Implementation", "src"));
        var publisher = new StubPublisher();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _root })
            .Build();
        var workspace = new WorkspaceTools(config, new WorkspacePathResolver(config), null!, null!);
        workspace.SetWorkspace("proj-key");
        var git = new GitTools(new CommandTools(config, workspace), config, publisher);

        var result = await git.OpenPullRequest("04_Implementation/src", "feature/x", "t", "b");

        Assert.Contains("not a git repository root", result);
        Assert.False(publisher.WasCalled);
    }

    // Tên nhánh vẫn được soát như cũ (chống argument-injection); repo hợp lệ hay không không làm mất chốt đó.
    [Fact]
    public async Task CreateBranch_WithUnsafeBranchName_IsBlocked()
    {
        var dir = Path.Combine(_workspacePath, "04_Implementation", "src");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));

        var result = await _git.CreateBranch("04_Implementation/src", "--upload-pack=evil", "main");

        Assert.Contains("Command blocked for security reason", result);
        Assert.Contains("invalid git branch name", result);
    }

    private sealed class StubPublisher : IPullRequestPublisher
    {
        public bool WasCalled { get; private set; }

        public Task<PullRequestPublishResult> PublishAsync(
            string? remoteUrl, string baseBranch, string headBranch, string title, string body,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new PullRequestPublishResult(false, null, "stub"));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
