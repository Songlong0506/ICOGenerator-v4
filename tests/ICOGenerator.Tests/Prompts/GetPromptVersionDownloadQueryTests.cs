using ICOGenerator.Application.Prompts;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Prompts;

// Export một phiên bản DB ra file .md: tên file mang cả số phiên bản để không nhầm với file gốc;
// phiên bản/key không tồn tại ⇒ null (404).
public class GetPromptVersionDownloadQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public GetPromptVersionDownloadQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.PromptTemplateVersions.Add(new PromptTemplateVersion
        {
            PromptKey = "BA/requirement-chat.v3.md",
            VersionNumber = 2,
            Content = "## nội dung v2"
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Execute_ReturnsContentWithVersionedFileName()
    {
        await using var db = NewDb();
        var vm = await new GetPromptVersionDownloadQuery(db).ExecuteAsync("BA/requirement-chat.v3.md", 2);

        Assert.NotNull(vm);
        Assert.Equal("requirement-chat.v3.db-v2.md", vm!.FileName);
        Assert.Equal("## nội dung v2", vm.Content);
    }

    [Fact]
    public async Task Execute_MissingVersionKeyOrInvalidNumber_ReturnsNull()
    {
        await using var db = NewDb();
        var sut = new GetPromptVersionDownloadQuery(db);

        Assert.Null(await sut.ExecuteAsync("BA/requirement-chat.v3.md", 99));
        Assert.Null(await sut.ExecuteAsync("BA/khac.md", 2));
        Assert.Null(await sut.ExecuteAsync("BA/requirement-chat.v3.md", 0));
        Assert.Null(await sut.ExecuteAsync(null, 2));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
