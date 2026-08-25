using ICOGenerator.Services.Artifacts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Chép thư mục workspace khi NHÂN BẢN dự án. Cùng hợp đồng true/false với TryRenameProjectWorkspace
// (true = "đã xong hoặc không có gì phải làm", false = "có dữ liệu nhưng không chép được"), thêm hai luật
// riêng: bản GỐC phải còn nguyên chỗ cũ, và các thư mục sinh lại được không đi theo bản sao.
public class LocalArtifactStorageCopyTests : IDisposable
{
    private const string SourceKey = "goc-1234abcd";
    private const string TargetKey = "ban-sao-9876fedc";

    private readonly string _root;
    private readonly LocalArtifactStorage _storage;
    private readonly WorkspacePathResolver _resolver;

    public LocalArtifactStorageCopyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ico-copy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _root })
            .Build();
        _resolver = new WorkspacePathResolver(config);
        _storage = new LocalArtifactStorage(_resolver, NullLogger<LocalArtifactStorage>.Instance);
    }

    [Fact]
    public void TryCopyProjectWorkspace_CopiesTheTree_AndLeavesTheSourceInPlace()
    {
        SeedSource();

        Assert.True(_storage.TryCopyProjectWorkspace(SourceKey, TargetKey));

        Assert.Equal("<html>poc</html>", File.ReadAllText(TargetPath("04_Implementation", "poc-demo.html")));
        Assert.Equal("cột A;cột B", File.ReadAllText(TargetPath("00_Source", "abcd", "quy-trinh.csv")));
        // Khác đổi tên: bản gốc KHÔNG được suy suyển.
        Assert.True(File.Exists(SourcePath("04_Implementation", "poc-demo.html")));
    }

    [Fact]
    public void TryCopyProjectWorkspace_SkipsRegenerableDirectories()
    {
        SeedSource();

        Assert.True(_storage.TryCopyProjectWorkspace(SourceKey, TargetKey));

        // node_modules/.git là phần lớn dung lượng của một workspace đã build và dựng lại được — chép
        // sang chỉ làm việc nhân bản chậm đi hàng phút.
        Assert.False(Directory.Exists(TargetPath("04_Implementation", "src", "backend", ".git")));
        Assert.False(Directory.Exists(TargetPath("04_Implementation", "src", "frontend", "node_modules")));
        // Nhưng code thật cạnh chúng thì phải sang.
        Assert.Equal("public class A {}", File.ReadAllText(TargetPath("04_Implementation", "src", "backend", "A.cs")));
    }

    [Fact]
    public void TryCopyProjectWorkspace_WithFolderFilter_TakesOnlyThoseTopLevelFolders()
    {
        SeedSource();

        Assert.True(_storage.TryCopyProjectWorkspace(SourceKey, TargetKey, new[] { "00_Source" }));

        Assert.True(File.Exists(TargetPath("00_Source", "abcd", "quy-trinh.csv")));
        Assert.False(Directory.Exists(TargetPath("04_Implementation")));
        // File nằm thẳng ở gốc workspace là sản phẩm sinh ra, không phải đầu vào của buổi phỏng vấn.
        Assert.False(File.Exists(TargetPath("ghi-chu.txt")));
    }

    [Fact]
    public void TryCopyProjectWorkspace_WithNothingOnDisk_Succeeds()
    {
        // Dự án gốc chưa sinh gì: không có dữ liệu để chép nên không được chặn việc nhân bản.
        Assert.True(_storage.TryCopyProjectWorkspace("never-created-1234abcd", TargetKey));
    }

    [Fact]
    public void TryCopyProjectWorkspace_WhenTargetExists_Fails_WithoutMerging()
    {
        SeedSource();
        _storage.InitializeProjectWorkspace(TargetKey);

        Assert.False(_storage.TryCopyProjectWorkspace(SourceKey, TargetKey));
        Assert.False(File.Exists(TargetPath("04_Implementation", "poc-demo.html")));
    }

    [Fact]
    public void TryCopyProjectWorkspace_OntoItself_Fails()
    {
        SeedSource();

        Assert.False(_storage.TryCopyProjectWorkspace(SourceKey, SourceKey));
        Assert.Equal("<html>poc</html>", File.ReadAllText(SourcePath("04_Implementation", "poc-demo.html")));
    }

    [Fact]
    public void TryCopyProjectWorkspace_WithoutConfiguredRoot_Succeeds()
    {
        var storage = new LocalArtifactStorage(
            new WorkspacePathResolver(new ConfigurationBuilder().Build()),
            NullLogger<LocalArtifactStorage>.Instance);

        Assert.True(storage.TryCopyProjectWorkspace(SourceKey, TargetKey));
    }

    [Fact]
    public void TryDeleteProjectWorkspace_RemovesTheFolder_AndIsSafeWhenItIsAlreadyGone()
    {
        SeedSource();

        _storage.TryDeleteProjectWorkspace(SourceKey);
        Assert.False(Directory.Exists(_resolver.GetProjectWorkspacePath(SourceKey)));

        // Gọi lại (hoặc gọi trên key chưa từng tồn tại) không được ném — đây là đường hoàn tác, nó chạy
        // trong lúc một ngoại lệ khác đang được ném lên.
        _storage.TryDeleteProjectWorkspace(SourceKey);
        _storage.TryDeleteProjectWorkspace("never-created-1234abcd");
    }

    private void SeedSource()
    {
        _storage.InitializeProjectWorkspace(SourceKey);

        Write(SourcePath("ghi-chu.txt"), "ghi chú ở gốc");
        Write(SourcePath("00_Source", "abcd", "quy-trinh.csv"), "cột A;cột B");
        Write(SourcePath("04_Implementation", "poc-demo.html"), "<html>poc</html>");
        Write(SourcePath("04_Implementation", "src", "backend", "A.cs"), "public class A {}");
        Write(SourcePath("04_Implementation", "src", "backend", ".git", "HEAD"), "ref: refs/heads/main");
        Write(SourcePath("04_Implementation", "src", "frontend", "node_modules", "pkg", "index.js"), "module.exports={}");
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string SourcePath(params string[] parts) =>
        Path.Combine(new[] { _resolver.GetProjectWorkspacePath(SourceKey) }.Concat(parts).ToArray());

    private string TargetPath(params string[] parts) =>
        Path.Combine(new[] { _resolver.GetProjectWorkspacePath(TargetKey) }.Concat(parts).ToArray());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* dọn rác best-effort */ }
    }
}
