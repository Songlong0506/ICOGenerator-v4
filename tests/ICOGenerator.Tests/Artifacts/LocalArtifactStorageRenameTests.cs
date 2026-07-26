using ICOGenerator.Services.Artifacts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Đổi tên thư mục workspace khi dự án được đổi tên (tên thư mục dẫn xuất từ tên dự án). Hợp đồng: true =
// "đã xong hoặc không có gì phải làm", false = "thư mục CÓ trên đĩa nhưng không chuyển được" — chỉ khi đó
// UpdateProjectUseCase mới hủy việc đổi tên để không bỏ rơi tài liệu đã sinh.
public class LocalArtifactStorageRenameTests : IDisposable
{
    private readonly string _root;
    private readonly LocalArtifactStorage _storage;
    private readonly WorkspacePathResolver _resolver;

    public LocalArtifactStorageRenameTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ico-rename-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _root })
            .Build();
        _resolver = new WorkspacePathResolver(config);
        _storage = new LocalArtifactStorage(_resolver, NullLogger<LocalArtifactStorage>.Instance);
    }

    [Fact]
    public void TryRenameProjectWorkspace_MovesFolderWithItsContent()
    {
        _storage.InitializeProjectWorkspace("old-key-1234abcd");
        var poc = Path.Combine(_resolver.GetProjectWorkspacePath("old-key-1234abcd"), "04_Implementation", "poc-demo.html");
        File.WriteAllText(poc, "<html>poc</html>");

        Assert.True(_storage.TryRenameProjectWorkspace("old-key-1234abcd", "new-key-1234abcd"));

        Assert.False(Directory.Exists(_resolver.GetProjectWorkspacePath("old-key-1234abcd")));
        Assert.Equal("<html>poc</html>", File.ReadAllText(
            Path.Combine(_resolver.GetProjectWorkspacePath("new-key-1234abcd"), "04_Implementation", "poc-demo.html")));
    }

    [Fact]
    public void TryRenameProjectWorkspace_WithNothingOnDisk_Succeeds()
    {
        // Dự án mới chưa sinh gì (hoặc RootPath sai nên khởi tạo thư mục đã thất bại lúc tạo project):
        // không có dữ liệu để mất nên không được chặn việc đổi tên.
        Assert.True(_storage.TryRenameProjectWorkspace("never-created-1234abcd", "new-key-1234abcd"));
    }

    [Fact]
    public void TryRenameProjectWorkspace_WithSameKey_Succeeds_AndKeepsFolder()
    {
        _storage.InitializeProjectWorkspace("same-key-1234abcd");

        Assert.True(_storage.TryRenameProjectWorkspace("same-key-1234abcd", "same-key-1234abcd"));
        Assert.True(Directory.Exists(_resolver.GetProjectWorkspacePath("same-key-1234abcd")));
    }

    [Fact]
    public void TryRenameProjectWorkspace_WhenTargetExists_Fails_WithoutMerging()
    {
        _storage.InitializeProjectWorkspace("old-key-1234abcd");
        _storage.InitializeProjectWorkspace("taken-key-1234abcd");

        Assert.False(_storage.TryRenameProjectWorkspace("old-key-1234abcd", "taken-key-1234abcd"));
        Assert.True(Directory.Exists(_resolver.GetProjectWorkspacePath("old-key-1234abcd")));
    }

    [Fact]
    public void TryRenameProjectWorkspace_WithoutConfiguredRoot_Succeeds()
    {
        var storage = new LocalArtifactStorage(
            new WorkspacePathResolver(new ConfigurationBuilder().Build()),
            NullLogger<LocalArtifactStorage>.Instance);

        Assert.True(storage.TryRenameProjectWorkspace("old-key-1234abcd", "new-key-1234abcd"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* dọn rác best-effort */ }
    }
}
