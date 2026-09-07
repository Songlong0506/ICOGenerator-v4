namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Đảm bảo MỖI repo đích của dự án (<see cref="ProjectRepositoryLayout"/>) là một git repo thật, trỏ
/// đúng remote CỦA DỰ ÁN, và có danh tính để commit được — trước khi Developer agent bắt đầu ghi code.
/// <para>
/// Vì sao phải có lớp này: các tool git của agent chạy trong thư mục repo, còn workspace thì chưa bao
/// giờ có repo nào — skeleton Bosch được clone từ repo TEMPLATE, và
/// <c>Project.BackendGitUrl</c>/<c>FrontendGitUrl</c> tuy bị cổng duyệt bắt buộc nhập nhưng không có
/// đường nào dẫn chúng tới git. Kết quả cũ: bước Pull Request hoặc báo "not a git repository", hoặc
/// (nếu template được clone thẳng vào chỗ code) đẩy nhánh feature lên chính REPO TEMPLATE. Lớp này là
/// chỗ nối hai đầu dây đó.
/// </para>
/// <para>
/// Idempotent: chạy lại (revision, retry, hoặc lần chạy thứ hai ở bước Pull Request) chỉ cập nhật
/// remote/danh tính, không bao giờ clone đè lên code agent đã ghi.
/// </para>
/// </summary>
public class ProjectRepositorySeeder
{
    private readonly IConfiguration _configuration;
    private readonly WorkspacePathResolver _workspacePathResolver;

    public ProjectRepositorySeeder(IConfiguration configuration, WorkspacePathResolver workspacePathResolver)
    {
        _configuration = configuration;
        _workspacePathResolver = workspacePathResolver;
    }

    public const string DefaultCommitterName = "ICOGenerator";
    public const string DefaultCommitterEmail = "icogenerator@localhost";

    /// <summary>
    /// Dựng/kiểm mọi repo đích của dự án. Trả về tóm tắt đọc được (đẩy vào feed tiến độ).
    /// Ném ngoại lệ nếu một lệnh clone đã cấu hình bị fail — cố ý dừng ngay ở bước Implementation thay
    /// vì để agent sinh 40 file rồi mới phát hiện không có repo nào để bàn giao ở bước cuối.
    /// </summary>
    public async Task<string> SeedAsync(
        string projectKey, IReadOnlyList<ProjectRepositorySlot> slots, CancellationToken cancellationToken)
    {
        var workspacePath = _workspacePathResolver.GetProjectWorkspacePath(projectKey);

        var results = new List<string>();
        foreach (var slot in slots)
            results.Add($"{slot.Label}: {await EnsureRepositoryAsync(workspacePath, slot, cancellationToken)}");

        return string.Join(" | ", results);
    }

    private async Task<string> EnsureRepositoryAsync(
        string workspacePath, ProjectRepositorySlot slot, CancellationToken cancellationToken)
    {
        var dir = _workspacePathResolver.GetSafeFullPath(workspacePath, slot.RelativePath);

        string outcome;
        if (IsGitRepository(dir))
        {
            outcome = "đã là repo";
        }
        else
        {
            Directory.CreateDirectory(dir);
            var empty = !Directory.EnumerateFileSystemEntries(dir).Any();

            if (empty && slot.RemoteUrl is not null)
            {
                // Clone repo THẬT của dự án: nhánh feature sau này mọc từ lịch sử của chính repo đích,
                // nên PR là một diff đọc được chứ không phải hai lịch sử không liên quan.
                var (exitCode, output) = await GitCli.RunAsync(
                    ["clone", slot.RemoteUrl, dir], workingDirectory: null, cancellationToken);
                if (exitCode != 0)
                    throw new InvalidOperationException(
                        $"git clone repo dự án ({slot.Label}) thất bại (exit {exitCode}): {output}");

                outcome = "đã clone repo dự án";
            }
            else
            {
                // Hai ca rơi vào đây: (a) chưa cấu hình URL repo — cổng duyệt chỉ đòi URL ở bước Pull
                // Request nên bước Implementation vẫn được chạy trước đó; (b) thư mục đã có file nhưng
                // không phải repo (workspace tạo trước khi có lớp này, hoặc agent đã ghi code rồi).
                // Cả hai đều cần một repo cục bộ để commit được; remote gắn thêm ở dưới nếu đã có URL.
                await InitRepositoryAsync(dir, cancellationToken);
                outcome = empty ? "đã git init (repo trống)" : "đã git init trên code có sẵn";
            }
        }

        var remoteNote = await EnsureRemoteAsync(dir, slot, cancellationToken);
        await EnsureCommitterIdentityAsync(dir, cancellationToken);
        return $"{outcome}, {remoteNote}";
    }

    private async Task InitRepositoryAsync(string dir, CancellationToken cancellationToken)
    {
        // "-b <base>": nhánh đầu tiên trùng nhánh đích của PR, để CreateBranch không phải checkout một
        // nhánh không tồn tại. Git < 2.28 không có cờ này ⇒ lùi về `git init` trần (tên nhánh mặc định
        // của máy chủ), vẫn commit và tạo nhánh feature được.
        var baseBranch = BaseBranch();
        var (exitCode, _) = await GitCli.RunAsync(["init", "-b", baseBranch], dir, cancellationToken);
        if (exitCode != 0)
            await GitCli.RunAsync(["init"], dir, cancellationToken);
    }

    private async Task<string> EnsureRemoteAsync(
        string dir, ProjectRepositorySlot slot, CancellationToken cancellationToken)
    {
        if (slot.RemoteUrl is null)
            return "chưa có remote (chưa nhập Git URL cho dự án)";

        var remoteName = RemoteName();

        // set-url trước: repo vừa clone đã có "origin", đặt lại là không-thao-tác; repo vừa init thì
        // lệnh này fail ("No such remote") và ta thêm mới. Thứ tự ngược lại cũng chạy, nhưng "add" fail
        // trên repo đã clone lại là ca THƯỜNG GẶP nhất — không nên để ca thường gặp đi qua đường lỗi.
        var (setExit, _) = await GitCli.RunAsync(["remote", "set-url", remoteName, slot.RemoteUrl], dir, cancellationToken);
        if (setExit == 0)
            return $"remote '{remoteName}' → repo dự án";

        var (addExit, addOutput) = await GitCli.RunAsync(["remote", "add", remoteName, slot.RemoteUrl], dir, cancellationToken);
        if (addExit != 0)
            throw new InvalidOperationException(
                $"Không gắn được remote '{remoteName}' cho repo {slot.Label} (exit {addExit}): {addOutput}");

        return $"remote '{remoteName}' → repo dự án";
    }

    /// <summary>
    /// Danh tính commit CỤC BỘ cho repo. Thiếu nó thì <c>git commit</c> fail với
    /// "Please tell me who you are" — máy chủ chạy app thường không có <c>user.email</c> toàn cục, và
    /// lỗi đó nổ ra ở tận bước cuối cùng của pipeline.
    /// Chỉ đặt khi CHƯA có giá trị nào (kể cả từ cấu hình toàn cục của máy chủ), để không giẫm lên danh
    /// tính mà người vận hành đã cố ý cấu hình.
    /// </summary>
    private async Task EnsureCommitterIdentityAsync(string dir, CancellationToken cancellationToken)
    {
        var (emailExit, _) = await GitCli.RunAsync(["config", "--get", "user.email"], dir, cancellationToken);
        if (emailExit != 0)
        {
            var email = _configuration["PullRequest:CommitterEmail"];
            await GitCli.RunAsync(
                ["config", "user.email", string.IsNullOrWhiteSpace(email) ? DefaultCommitterEmail : email],
                dir, cancellationToken);
        }

        var (nameExit, _) = await GitCli.RunAsync(["config", "--get", "user.name"], dir, cancellationToken);
        if (nameExit != 0)
        {
            var name = _configuration["PullRequest:CommitterName"];
            await GitCli.RunAsync(
                ["config", "user.name", string.IsNullOrWhiteSpace(name) ? DefaultCommitterName : name],
                dir, cancellationToken);
        }
    }

    // ".git" là thư mục ở repo thường, là FILE ở worktree/submodule — cùng luật với GitTools.ValidateRepo.
    private static bool IsGitRepository(string dir)
    {
        var gitPath = Path.Combine(dir, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath);
    }

    private string RemoteName()
    {
        var name = _configuration["PullRequest:RemoteName"];
        return string.IsNullOrWhiteSpace(name) ? "origin" : name;
    }

    private string BaseBranch()
    {
        var branch = _configuration["PullRequest:BaseBranch"];
        return string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-') ? "main" : branch;
    }
}
