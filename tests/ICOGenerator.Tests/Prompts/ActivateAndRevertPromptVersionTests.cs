using ICOGenerator.Application.Prompts;
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

// Rollback (kích hoạt bản cũ) và quay-về-file: đổi đúng cờ IsActive (nhiều nhất MỘT bản active mỗi
// key), không tạo snapshot mới, và có hiệu lực ngay qua provider (cache được invalidate).
public class ActivateAndRevertPromptVersionTests : IDisposable
{
    private const string Key = "BA/a.md";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly Guid _v1Id = Guid.NewGuid();
    private readonly Guid _v2Id = Guid.NewGuid();

    public ActivateAndRevertPromptVersionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.PromptTemplateVersions.AddRange(
            new PromptTemplateVersion { Id = _v1Id, PromptKey = Key, VersionNumber = 1, Content = "v1" },
            new PromptTemplateVersion { Id = _v2Id, PromptKey = Key, VersionNumber = 2, Content = "v2", IsActive = true });
        db.SaveChanges();
    }

    [Fact]
    public async Task Activate_MovesActiveFlag_AndTakesEffectImmediately()
    {
        await using var db = NewDb();
        var provider = NewProvider(db);
        Assert.Equal("v2", provider.GetActiveOverride(Key)!.Content); // mồi cache để chứng minh invalidate

        var promptKey = await new ActivatePromptVersionUseCase(db, provider, new SavePromptVersionUseCaseTests.NullAuditLogger())
            .ExecuteAsync(_v1Id);

        Assert.Equal(Key, promptKey);
        var versions = await db.PromptTemplateVersions.OrderBy(v => v.VersionNumber).ToListAsync();
        Assert.Equal(new[] { true, false }, versions.Select(v => v.IsActive));
        Assert.Equal(2, versions.Count); // rollback không tạo snapshot mới
        Assert.Equal("v1", provider.GetActiveOverride(Key)!.Content);
    }

    [Fact]
    public async Task Activate_UnknownId_ReturnsNull()
    {
        await using var db = NewDb();
        var result = await new ActivatePromptVersionUseCase(db, NewProvider(db), new SavePromptVersionUseCaseTests.NullAuditLogger())
            .ExecuteAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task RevertToFile_DeactivatesAllVersions_ProviderReturnsNull()
    {
        await using var db = NewDb();
        var provider = NewProvider(db);
        Assert.NotNull(provider.GetActiveOverride(Key));

        await new RevertPromptToFileUseCase(db, provider, new SavePromptVersionUseCaseTests.NullAuditLogger())
            .ExecuteAsync(Key);

        Assert.All(await db.PromptTemplateVersions.ToListAsync(), v => Assert.False(v.IsActive));
        // Không còn override ⇒ PromptTemplateService.Get sẽ rơi về nội dung file.
        Assert.Null(provider.GetActiveOverride(Key));
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
