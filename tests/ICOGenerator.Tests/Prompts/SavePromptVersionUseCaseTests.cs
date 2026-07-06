using ICOGenerator.Application.Prompts;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Prompts;

// Lưu phiên bản prompt: lần sửa đầu chụp thêm bản gốc từ file làm v1, bản mới active ngay và
// PromptTemplateService.Get đổi nội dung NGAY (cache provider được invalidate); nội dung trùng bản
// đang dùng thì không snapshot; key không tồn tại bị chặn.
public class SavePromptVersionUseCaseTests : IDisposable
{
    private readonly string _root;
    // Key duy nhất mỗi lần chạy: cache nội dung file của PromptTemplateService là static toàn tiến trình.
    private readonly string _key = $"Test/{Guid.NewGuid():N}.md";
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public SavePromptVersionUseCaseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ico-prompt-tests-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(_root, "Prompts", _key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "dòng 1\ndòng 2");

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task FirstSave_CapturesFileBaselineAsV1_ActivatesNewAsV2_AndTakesEffectImmediately()
    {
        await using var db = NewDb();
        var provider = NewProvider(db);
        var templates = NewTemplates(provider);

        var result = await NewSut(db, provider, templates)
            .ExecuteAsync(_key, "dòng 1\ndòng 2 ĐÃ SỬA", "siết điều kiện", "teamdev");

        Assert.Equal(SavePromptVersionResult.Saved, result);

        var versions = await db.PromptTemplateVersions.Where(v => v.PromptKey == _key).OrderBy(v => v.VersionNumber).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal("dòng 1\ndòng 2", versions[0].Content);          // v1 = bản gốc chụp từ file
        Assert.False(versions[0].IsActive);
        Assert.Equal("Bản gốc chụp từ file", versions[0].ChangeNote);
        Assert.True(versions[1].IsActive);
        Assert.Equal("siết điều kiện", versions[1].ChangeNote);
        Assert.Equal("teamdev", versions[1].CreatedByUsername);

        // Cache provider đã invalidate ⇒ lời gọi LLM kế tiếp (PromptTemplateService.Get) thấy bản mới NGAY.
        Assert.Equal("dòng 1\ndòng 2 ĐÃ SỬA", templates.Get(_key));
    }

    [Fact]
    public async Task SecondSave_IncrementsVersion_AndMovesActiveFlag()
    {
        await using var db = NewDb();
        var provider = NewProvider(db);
        var sut = NewSut(db, provider, NewTemplates(provider));

        await sut.ExecuteAsync(_key, "bản A", null, "teamdev");
        var result = await sut.ExecuteAsync(_key, "bản B", null, "teamdev");

        Assert.Equal(SavePromptVersionResult.Saved, result);
        var versions = await db.PromptTemplateVersions.Where(v => v.PromptKey == _key).OrderBy(v => v.VersionNumber).ToListAsync();
        Assert.Equal(3, versions.Count); // v1 baseline + v2 + v3
        Assert.Equal(new[] { false, false, true }, versions.Select(v => v.IsActive));
        Assert.Equal("bản B", versions[2].Content);
    }

    [Fact]
    public async Task SaveUnchangedContent_ReturnsNoChange_WithoutSnapshot()
    {
        await using var db = NewDb();
        var provider = NewProvider(db);
        var sut = NewSut(db, provider, NewTemplates(provider));

        // CRLF từ textarea được chuẩn hoá về LF nên trùng khít nội dung file ⇒ NoChange, không tạo version nào.
        var result = await sut.ExecuteAsync(_key, "dòng 1\r\ndòng 2", null, "teamdev");

        Assert.Equal(SavePromptVersionResult.NoChange, result);
        Assert.Empty(await db.PromptTemplateVersions.ToListAsync());
    }

    [Fact]
    public async Task SaveRejectsUnknownKeyAndEmptyContent()
    {
        await using var db = NewDb();
        var provider = NewProvider(db);
        var sut = NewSut(db, provider, NewTemplates(provider));

        Assert.Equal(SavePromptVersionResult.UnknownPromptKey, await sut.ExecuteAsync("Nope/khong-ton-tai.md", "x", null, null));
        Assert.Equal(SavePromptVersionResult.InvalidInput, await sut.ExecuteAsync(_key, "   ", null, null));
        Assert.Empty(await db.PromptTemplateVersions.ToListAsync());
    }

    private SavePromptVersionUseCase NewSut(AppDbContext db, IPromptOverrideProvider provider, PromptTemplateService templates) =>
        new(db, new PromptFileCatalog(NewEnv()), templates, provider, new NullAuditLogger());

    private PromptTemplateService NewTemplates(IPromptOverrideProvider provider) => new(NewEnv(), provider);

    private DbPromptOverrideProvider NewProvider(AppDbContext db) =>
        new(db, _cache, NullLogger<DbPromptOverrideProvider>.Instance);

    private IWebHostEnvironment NewEnv() => new FakeWebHostEnvironment { ContentRootPath = _root };

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        _cache.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    internal sealed class NullAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditCategory category, AuditAction action, string entityId, string summary,
            object? before = null, object? after = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
