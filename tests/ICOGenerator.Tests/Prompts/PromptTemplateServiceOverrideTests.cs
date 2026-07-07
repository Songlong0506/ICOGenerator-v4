using ICOGenerator.Services.Prompts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace ICOGenerator.Tests.Prompts;

// PromptTemplateService.Get: bản DB active (qua IPromptOverrideProvider) THAY nội dung file; provider
// trả null (không có bản active / lỗi đã nuốt) thì rơi về file — đường file luôn là chỗ rơi an toàn.
public class PromptTemplateServiceOverrideTests : IDisposable
{
    private readonly string _root;
    // Key duy nhất mỗi lần chạy: cache nội dung file của PromptTemplateService là static toàn tiến trình.
    private readonly string _key = $"Test/{Guid.NewGuid():N}.md";

    public PromptTemplateServiceOverrideTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ico-prompt-tests-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(_root, "Prompts", _key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "nội dung file");
    }

    [Fact]
    public void Get_UsesActiveOverride_WhenProviderHasOne()
    {
        var sut = new PromptTemplateService(NewEnv(), new FixedProvider(new PromptOverride(Guid.NewGuid(), 2, "nội dung DB v2")));

        Assert.Equal("nội dung DB v2", sut.Get(_key));
        // GetFileContent luôn đọc FILE, bỏ qua override — Prompt Studio dùng làm baseline.
        Assert.Equal("nội dung file", sut.GetFileContent(_key));
    }

    [Fact]
    public void Get_FallsBackToFile_WhenNoOverride()
    {
        Assert.Equal("nội dung file", new PromptTemplateService(NewEnv(), new FixedProvider(null)).Get(_key));
        // Không truyền provider (mặc định null — như test stub kế thừa) cũng đi đường file.
        Assert.Equal("nội dung file", new PromptTemplateService(NewEnv()).Get(_key));
    }

    private IWebHostEnvironment NewEnv() => new FakeWebHostEnvironment { ContentRootPath = _root };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FixedProvider : IPromptOverrideProvider
    {
        private readonly PromptOverride? _override;
        public FixedProvider(PromptOverride? @override) => _override = @override;
        public PromptOverride? GetActiveOverride(string promptKey) => _override;
        public void Invalidate() { }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }
}
