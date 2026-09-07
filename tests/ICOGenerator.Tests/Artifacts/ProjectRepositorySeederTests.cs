using ICOGenerator.Services.Artifacts;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Các test này GỌI GIT THẬT (init / remote / config) trong một thư mục tạm — không lệnh nào chạm mạng,
// nên chúng chạy được ở mọi nơi app chạy được (app vốn đã yêu cầu máy chủ có git).
// Đường CLONE cố tình KHÔNG được test ở đây: nó cần một remote thật, và một test phụ thuộc mạng thì
// hỏng vì lý do chẳng liên quan gì tới thứ nó định giữ. Vì vậy mọi ca dưới đây đặt sẵn một file trong
// thư mục (hoặc bỏ trống URL) để rơi vào nhánh "dựng repo tại chỗ".
public class ProjectRepositorySeederTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"icogen-repo-seed-{Guid.NewGuid():N}");
    private readonly IConfiguration _config;
    private readonly WorkspacePathResolver _resolver;

    public ProjectRepositorySeederTests()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _root })
            .Build();
        _resolver = new WorkspacePathResolver(_config);
    }

    // Ca "workspace cũ": code đã nằm sẵn trên đĩa nhưng thư mục chưa bao giờ là repo (mọi workspace tạo
    // trước khi có lớp này đều như vậy). Phải dựng repo NGAY TẠI CHỖ và giữ nguyên code — clone đè lên
    // là mất trắng sản phẩm của cả bước Implementation.
    [Fact]
    public async Task SeedAsync_InitialisesRepoInPlace_AndKeepsExistingCode()
    {
        var slot = SourceSlot("https://git.example.com/app.git");
        var dir = PrepareSlotDirectory(slot);

        var summary = await NewSeeder().SeedAsync("proj-key", [slot], CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(dir, ".git")));
        Assert.True(File.Exists(Path.Combine(dir, "Program.cs")));
        Assert.Contains("git init trên code có sẵn", summary);
    }

    // Remote phải trỏ về repo CỦA DỰ ÁN. Đây là đích của cả thay đổi này: trước đây
    // Project.BackendGitUrl chỉ bị cổng duyệt kiểm tra rồi nằm im, nên bước Pull Request đẩy nhánh lên
    // bất cứ remote nào tình cờ có trong thư mục (repo template Bosch đã clone) — hoặc không có remote nào.
    [Fact]
    public async Task SeedAsync_PointsRemoteAtTheProjectRepository()
    {
        var slot = SourceSlot("https://git.example.com/app.git");
        var dir = PrepareSlotDirectory(slot);

        var summary = await NewSeeder().SeedAsync("proj-key", [slot], CancellationToken.None);

        Assert.Equal("https://git.example.com/app.git", await RemoteUrlAsync(dir));
        Assert.Contains("remote 'origin' → repo dự án", summary);
    }

    // Thiếu danh tính commit thì `git commit` fail với "Please tell me who you are" — ở tận bước CUỐI của
    // pipeline, sau khi đã trả tiền cho mọi bước trước. Sau khi seed, repo luôn phải có danh tính (của
    // máy chủ nếu đã cấu hình, nếu chưa thì mặc định của app).
    [Fact]
    public async Task SeedAsync_LeavesRepoWithACommitterIdentity()
    {
        var slot = SourceSlot(null);
        var dir = PrepareSlotDirectory(slot);

        await NewSeeder().SeedAsync("proj-key", [slot], CancellationToken.None);

        var (emailExit, email) = await GitCli.RunAsync(["config", "--get", "user.email"], dir, CancellationToken.None);
        var (nameExit, _) = await GitCli.RunAsync(["config", "--get", "user.name"], dir, CancellationToken.None);
        Assert.Equal(0, emailExit);
        Assert.Equal(0, nameExit);
        Assert.False(string.IsNullOrWhiteSpace(email));
    }

    // Chưa nhập Git URL vẫn phải dựng được repo cục bộ: cổng duyệt chỉ đòi URL ngay trước bước Pull
    // Request, nên bước Implementation chạy trước đó thường chưa có URL nào. Commit được là đủ; phần
    // thiếu remote được nói thẳng trong tóm tắt để nó hiện lên feed tiến độ.
    [Fact]
    public async Task SeedAsync_WithoutUrl_StillCreatesRepo_AndSaysRemoteIsMissing()
    {
        var slot = SourceSlot(null);
        var dir = PrepareSlotDirectory(slot, withCode: false);

        var summary = await NewSeeder().SeedAsync("proj-key", [slot], CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(dir, ".git")));
        Assert.Contains("git init (repo trống)", summary);
        Assert.Contains("chưa có remote", summary);
    }

    // Idempotent: seeder chạy lại ở mỗi vòng chỉnh sửa, mỗi lần retry và một lần nữa ở bước Pull Request.
    // Lần sau chỉ được cập nhật remote — không đụng code, không dựng lại repo.
    [Fact]
    public async Task SeedAsync_IsIdempotent_AndRefreshesRemoteUrl()
    {
        var first = SourceSlot(null);
        var dir = PrepareSlotDirectory(first);
        await NewSeeder().SeedAsync("proj-key", [first], CancellationToken.None);

        // Lần hai: URL vừa được TeamDev điền ở Agent Dashboard sau khi code đã sinh xong.
        var second = first with { RemoteUrl = "https://git.example.com/app.git" };
        var summary = await NewSeeder().SeedAsync("proj-key", [second], CancellationToken.None);

        Assert.Contains("đã là repo", summary);
        Assert.True(File.Exists(Path.Combine(dir, "Program.cs")));
        Assert.Equal("https://git.example.com/app.git", await RemoteUrlAsync(dir));
    }

    // Khung Bosch: hai repo tách rời, mỗi repo một remote riêng — không được gộp thành một.
    [Fact]
    public async Task SeedAsync_BoschTemplate_PreparesBothRepositoriesSeparately()
    {
        var slots = ProjectRepositoryLayout.Resolve(
            useBoschTemplate: true,
            backendGitUrl: "https://git.example.com/be.git",
            frontendGitUrl: "https://git.example.com/fe.git");
        foreach (var slot in slots)
            PrepareSlotDirectory(slot);

        await NewSeeder().SeedAsync("proj-key", slots, CancellationToken.None);

        Assert.Equal("https://git.example.com/be.git", await RemoteUrlAsync(SlotPath(slots[0])));
        Assert.Equal("https://git.example.com/fe.git", await RemoteUrlAsync(SlotPath(slots[1])));
    }

    private static ProjectRepositorySlot SourceSlot(string? remoteUrl) =>
        new("src", ProjectRepositoryLayout.SourceRelativePath, remoteUrl);

    private ProjectRepositorySeeder NewSeeder() => new(_config, _resolver);

    private string SlotPath(ProjectRepositorySlot slot) =>
        Path.Combine(_resolver.GetProjectWorkspacePath("proj-key"),
            slot.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    // withCode = true: thư mục có sẵn file ⇒ seeder đi nhánh "dựng repo tại chỗ" (không chạm mạng).
    private string PrepareSlotDirectory(ProjectRepositorySlot slot, bool withCode = true)
    {
        var dir = SlotPath(slot);
        Directory.CreateDirectory(dir);
        if (withCode)
            File.WriteAllText(Path.Combine(dir, "Program.cs"), "// code");
        return dir;
    }

    private static async Task<string> RemoteUrlAsync(string dir)
    {
        var (_, output) = await GitCli.RunAsync(["remote", "get-url", "origin"], dir, CancellationToken.None);
        return output.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
