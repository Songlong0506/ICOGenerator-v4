using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Prompts;

// Provider bản prompt DB active: trả đúng bản active theo key, cache theo TTL nhưng Invalidate có hiệu
// lực NGAY, và fail-open (DB lỗi ⇒ null ⇒ PromptTemplateService rơi về file) — bất biến giữ cho mọi
// lời gọi LLM không bao giờ gãy vì bảng phiên bản.
public class DbPromptOverrideProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public DbPromptOverrideProviderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public void GetActiveOverride_ReturnsActiveVersion_NullWhenNoneActive()
    {
        var activeId = Guid.NewGuid();
        using (var db = NewDb())
        {
            db.PromptTemplateVersions.AddRange(
                new PromptTemplateVersion { PromptKey = "BA/a.md", VersionNumber = 1, Content = "v1", IsActive = false },
                new PromptTemplateVersion { Id = activeId, PromptKey = "BA/a.md", VersionNumber = 2, Content = "v2", IsActive = true },
                new PromptTemplateVersion { PromptKey = "BA/b.md", VersionNumber = 1, Content = "b1", IsActive = false });
            db.SaveChanges();
        }

        var provider = NewProvider(NewDb());

        var found = provider.GetActiveOverride("BA/a.md");
        Assert.NotNull(found);
        Assert.Equal(activeId, found!.Id);
        Assert.Equal(2, found.VersionNumber);
        Assert.Equal("v2", found.Content);

        Assert.Null(provider.GetActiveOverride("BA/b.md"));      // có version nhưng không bản nào active
        Assert.Null(provider.GetActiveOverride("BA/khac.md"));   // không có version nào
    }

    [Fact]
    public void GetActiveOverride_IsCached_UntilInvalidate()
    {
        using (var db = NewDb())
        {
            db.PromptTemplateVersions.Add(new PromptTemplateVersion { PromptKey = "BA/a.md", VersionNumber = 1, Content = "cũ", IsActive = true });
            db.SaveChanges();
        }

        var provider = NewProvider(NewDb());
        Assert.Equal("cũ", provider.GetActiveOverride("BA/a.md")!.Content);

        // Đổi bản active trong DB: cache còn TTL nên vẫn thấy bản cũ; Invalidate xong thấy ngay bản mới.
        using (var db = NewDb())
        {
            var old = db.PromptTemplateVersions.Single();
            old.IsActive = false;
            db.PromptTemplateVersions.Add(new PromptTemplateVersion { PromptKey = "BA/a.md", VersionNumber = 2, Content = "mới", IsActive = true });
            db.SaveChanges();
        }

        Assert.Equal("cũ", provider.GetActiveOverride("BA/a.md")!.Content);
        provider.Invalidate();
        Assert.Equal("mới", provider.GetActiveOverride("BA/a.md")!.Content);
    }

    [Fact]
    public void GetActiveOverride_DbFailure_FailsOpenToNull()
    {
        var db = NewDb();
        db.Dispose(); // context đã dispose ⇒ query ném lỗi ⇒ provider phải nuốt và trả null

        var provider = NewProvider(db);
        Assert.Null(provider.GetActiveOverride("BA/a.md"));
    }

    private DbPromptOverrideProvider NewProvider(AppDbContext db) =>
        new(db, _cache, NullLogger<DbPromptOverrideProvider>.Instance);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        _cache.Dispose();
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
