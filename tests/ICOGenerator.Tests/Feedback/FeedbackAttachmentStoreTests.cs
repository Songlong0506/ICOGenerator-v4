using System.Text;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Feedback;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Feedback;

// File đính kèm feedback nằm dưới AgentWorkspace:RootPath (không còn key Feedback:UploadRootPath riêng).
// RootPath sửa được từ màn Settings khi app đang chạy, nên hai điều phải giữ:
//   • ghi theo thư mục gốc HIỆN TẠI (đọc lại config mỗi lần, không cache lúc khởi tạo);
//   • xóa theo StoredPath đã lưu — ghép lại từ gốc hiện tại thì phản hồi gửi trước lần đổi gốc để lại file mồ côi.
public class FeedbackAttachmentStoreTests : IDisposable
{
    private readonly string _rootA = Path.Combine(Path.GetTempPath(), $"icogen-fb-a-{Guid.NewGuid():N}");
    private readonly string _rootB = Path.Combine(Path.GetTempPath(), $"icogen-fb-b-{Guid.NewGuid():N}");
    private readonly IConfigurationRoot _config;
    private readonly FeedbackAttachmentStore _store;

    public FeedbackAttachmentStoreTests()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _rootA })
            .Build();
        _store = new FeedbackAttachmentStore(new WorkspacePathResolver(_config), NullLogger<FeedbackAttachmentStore>.Instance);
    }

    [Fact]
    public async Task StoreAsync_WritesUnderHiddenFolderOfWorkspaceRoot()
    {
        var feedbackId = Guid.NewGuid();

        var attachments = await _store.StoreAsync(feedbackId, [File("anh.png")]);

        var expectedDir = Path.Combine(_rootA, WorkspacePathResolver.FeedbackUploadsFolder, feedbackId.ToString("N"));
        var stored = Assert.Single(attachments).StoredPath;
        Assert.Equal(expectedDir, Path.GetDirectoryName(stored));
        Assert.True(System.IO.File.Exists(stored));
    }

    [Fact]
    public async Task StoreAsync_FollowsRootPathChangedAtRuntime()
    {
        _config["AgentWorkspace:RootPath"] = _rootB;

        var attachments = await _store.StoreAsync(Guid.NewGuid(), [File("tai-lieu.pdf")]);

        Assert.StartsWith(_rootB, Assert.Single(attachments).StoredPath);
    }

    [Fact]
    public async Task DeleteFiles_AfterRootPathChanged_RemovesFolderUnderOldRoot()
    {
        var feedbackId = Guid.NewGuid();
        var attachments = await _store.StoreAsync(feedbackId, [File("anh.png")]);
        var oldDir = Path.GetDirectoryName(attachments[0].StoredPath)!;

        _config["AgentWorkspace:RootPath"] = _rootB;
        _store.DeleteFiles(feedbackId, attachments.Select(a => a.StoredPath));

        Assert.False(Directory.Exists(oldDir));
    }

    // StoredPath hỏng/lạ không bao giờ được kéo theo một thư mục không mang đúng tên {feedbackId:N}.
    [Fact]
    public void DeleteFiles_IgnoresStoredPathOutsideFeedbackFolder()
    {
        var unrelated = Path.Combine(_rootA, "du-an-12345678", "01_Requirement");
        Directory.CreateDirectory(unrelated);
        var foreignFile = Path.Combine(unrelated, "brief.md");
        System.IO.File.WriteAllText(foreignFile, "x");

        _store.DeleteFiles(Guid.NewGuid(), [foreignFile]);

        Assert.True(System.IO.File.Exists(foreignFile));
    }

    [Fact]
    public async Task StoreAsync_TooManyFiles_RejectsWithoutWriting()
    {
        var feedbackId = Guid.NewGuid();
        var files = Enumerable.Range(0, FeedbackAttachmentStore.MaxFiles + 1).Select(i => File($"f{i}.png")).ToList();

        await Assert.ThrowsAsync<FeedbackValidationException>(() => _store.StoreAsync(feedbackId, files));

        Assert.False(Directory.Exists(Path.Combine(_rootA, WorkspacePathResolver.FeedbackUploadsFolder, feedbackId.ToString("N"))));
    }

    private static IFormFile File(string name)
    {
        var bytes = Encoding.UTF8.GetBytes("noi dung");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name) { Headers = new HeaderDictionary() };
    }

    public void Dispose()
    {
        foreach (var root in new[] { _rootA, _rootB })
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
    }
}
