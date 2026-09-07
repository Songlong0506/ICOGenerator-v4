using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.PullRequests;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICOGenerator.Tests.Tools;

// Chạy TRỌN chuỗi bàn giao trên một repo THẬT — chỉ khác một điểm với môi trường chạy thật: "repo của
// dự án" là một bare repo trên đĩa (`file://`), nên không cần mạng lẫn credential.
//
// Đây là test duy nhất chứng minh cái đã hỏng: seeder clone repo dự án → agent commit → push LÊN ĐÚNG
// repo đó. Trước đây không có mảnh nào của chuỗi này được test, và cả chuỗi thì không chạy được (lệnh
// git chạy ở gốc workspace, nơi không có repo nào).
public class GitHandoffEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"icogen-handoff-{Guid.NewGuid():N}");

    [Fact]
    public async Task Seeder_ThenGitTools_PushesTheFeatureBranchToTheProjectRepository()
    {
        // "Repo của dự án": bare repo TRỐNG — đúng ca thường gặp nhất (repo vừa được tạo cho sản phẩm mới).
        var projectRepo = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(projectRepo);
        Assert.Equal(0, (await GitCli.RunAsync(["init", "--bare", "-b", "main"], projectRepo, CancellationToken.None)).ExitCode);

        var config = BuildConfig();
        var resolver = new WorkspacePathResolver(config);

        // 1) Worker dựng repo đích trước bước Implementation.
        var slots = ProjectRepositoryLayout.Resolve(useBoschTemplate: false, backendGitUrl: projectRepo, frontendGitUrl: null);
        var summary = await new ProjectRepositorySeeder(config, resolver).SeedAsync("proj-key", slots, CancellationToken.None);
        Assert.Contains("đã clone repo dự án", summary);

        // 2) Agent ghi code vào repo đó (ở đây viết thẳng file cho gọn — WriteFile đã có test riêng).
        var repoPath = slots[0].RelativePath;
        var repoDir = Path.Combine(resolver.GetProjectWorkspacePath("proj-key"),
            repoPath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(Path.Combine(repoDir, "Program.cs"), "// sản phẩm của bước Implementation");

        // 3) Bước Pull Request: đúng chuỗi tool mà prompt bàn giao yêu cầu.
        var git = NewGitTools(config, resolver);
        Assert.Contains("Repo: " + repoPath, await git.CreateBranch(repoPath, "feature/demo", "main"));
        Assert.Contains("Repo: " + repoPath, await git.GitCommit(repoPath, "feat: bàn giao demo"));
        var prResult = await git.OpenPullRequest(repoPath, "feature/demo", "Demo", "Nội dung PR");

        // 4) Nhánh phải nằm trong REPO CỦA DỰ ÁN — thứ mà cả đường cũ không bao giờ chạm tới được.
        var (branchExit, branches) = await GitCli.RunAsync(["branch", "--list"], projectRepo, CancellationToken.None);
        Assert.Equal(0, branchExit);
        Assert.Contains("feature/demo", branches);

        // File đã commit thật sự đi kèm nhánh đó (push rỗng cũng "thành công" nếu không kiểm nội dung).
        var (showExit, tree) = await GitCli.RunAsync(
            ["ls-tree", "--name-only", "feature/demo"], projectRepo, CancellationToken.None);
        Assert.Equal(0, showExit);
        Assert.Contains("Program.cs", tree);

        // Remote mà bước PR đọc để dựng link/gọi API là repo dự án, không phải repo nào khác.
        Assert.Contains(projectRepo, prResult);
    }

    // Repo chưa có Git URL: vẫn phải commit được (không mất việc), và bước PR nói rõ là không đẩy được
    // — thay vì báo thành công rồi để mọi người đi tìm một PR không tồn tại.
    [Fact]
    public async Task WithoutRemote_CommitWorks_ButPushReportsFailure()
    {
        var config = BuildConfig();
        var resolver = new WorkspacePathResolver(config);

        var slots = ProjectRepositoryLayout.Resolve(useBoschTemplate: false, backendGitUrl: null, frontendGitUrl: null);
        var repoPath = slots[0].RelativePath;
        var repoDir = Path.Combine(resolver.GetProjectWorkspacePath("proj-key"),
            repoPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(repoDir);
        await File.WriteAllTextAsync(Path.Combine(repoDir, "Program.cs"), "// code");

        await new ProjectRepositorySeeder(config, resolver).SeedAsync("proj-key", slots, CancellationToken.None);

        var git = NewGitTools(config, resolver);
        await git.CreateBranch(repoPath, "feature/demo", "main");
        var commit = await git.GitCommit(repoPath, "feat: demo");
        var prResult = await git.OpenPullRequest(repoPath, "feature/demo", "Demo", "Nội dung PR");

        Assert.Contains("ExitCode: 0", commit);                  // commit thành công
        Assert.Contains("không đọc được remote", prResult);      // nhưng không có chỗ nào để đẩy lên
    }

    private static GitTools NewGitTools(IConfiguration config, WorkspacePathResolver resolver)
    {
        // Hai dependency POC không nằm trên đường đi của các tool git (xem GitToolsRepoScopeTests).
        var workspace = new WorkspaceTools(config, resolver, null!, null!);
        workspace.SetWorkspace("proj-key");
        return new GitTools(new CommandTools(config, workspace), config, new NoopPublisher());
    }

    private IConfiguration BuildConfig()
    {
        var values = new Dictionary<string, string?>
        {
            ["AgentWorkspace:RootPath"] = _root,
            ["PullRequest:RemoteName"] = "origin",
            ["PullRequest:BaseBranch"] = "main"
        };

        // Cùng allowlist với appsettings thật — chuỗi bàn giao phải chạy được bằng ĐÚNG các lệnh đã cho phép.
        var allowed = new[] { "git status", "git diff", "git add", "git commit", "git push", "git checkout", "git remote get-url" };
        for (var i = 0; i < allowed.Length; i++)
            values[$"AllowedCommands:{i}"] = allowed[i];

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // Không có token/không phải GitHub ⇒ đường tạo PR thật không chạy; test này đo phần git, không đo API.
    private sealed class NoopPublisher : IPullRequestPublisher
    {
        public Task<PullRequestPublishResult> PublishAsync(
            string? remoteUrl, string baseBranch, string headBranch, string title, string body,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PullRequestPublishResult(false, null, "không cấu hình token"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
