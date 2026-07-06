using ICOGenerator.Application.Prompts;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace ICOGenerator.Tests.Prompts;

// Diff prompt: mốc 0 = nội dung FILE trong repo, mốc n = phiên bản DB; mốc không tồn tại ⇒ null (404).
public class GetPromptVersionDiffQueryTests : IDisposable
{
    private readonly string _root;
    // Key duy nhất mỗi lần chạy: cache nội dung file của PromptTemplateService là static toàn tiến trình.
    private readonly string _key = $"Test/{Guid.NewGuid():N}.md";
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public GetPromptVersionDiffQueryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ico-prompt-tests-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(_root, "Prompts", _key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "giữ nguyên\ndòng cũ");

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.PromptTemplateVersions.Add(new PromptTemplateVersion
        {
            PromptKey = _key,
            VersionNumber = 1,
            Content = "giữ nguyên\ndòng mới"
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Diff_FileToVersion_LabelsAndLineKindsCorrect()
    {
        await using var db = NewDb();
        var vm = await NewSut(db).ExecuteAsync(_key, fromVersion: 0, toVersion: 1);

        Assert.NotNull(vm);
        Assert.Equal("file", vm!.FromLabel);
        Assert.Equal("v1", vm.ToLabel);
        Assert.Contains(vm.Lines, l => l.Kind == DiffLineKind.Same && l.Text == "giữ nguyên");
        Assert.Contains(vm.Lines, l => l.Kind == DiffLineKind.Removed && l.Text == "dòng cũ");
        Assert.Contains(vm.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "dòng mới");
    }

    [Fact]
    public async Task Diff_MissingVersionOrKey_ReturnsNull()
    {
        await using var db = NewDb();
        var sut = NewSut(db);

        Assert.Null(await sut.ExecuteAsync(_key, 0, 99));                 // phiên bản không tồn tại
        Assert.Null(await sut.ExecuteAsync("Nope/khong-co.md", 0, 1));    // template không tồn tại
        Assert.Null(await sut.ExecuteAsync(_key, -1, 1));                 // mốc âm
    }

    private GetPromptVersionDiffQuery NewSut(AppDbContext db)
    {
        var env = new FakeWebHostEnvironment { ContentRootPath = _root };
        return new GetPromptVersionDiffQuery(db, new PromptFileCatalog(env), new PromptTemplateService(env), new DocumentDiffService());
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
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

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
