using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Link chia sẻ bản demo cho người KHÔNG có tài khoản — đây là bề mặt duy nhất người lạ chạm được, nên
// các test tập trung vào chính những thứ giữ nó an toàn: token phải ngẫu nhiên và duy nhất, LUÔN có hạn
// dùng (kẹp trần), thu hồi được, và mọi trường hợp không hợp lệ đều trả về CÙNG một kết quả rỗng để
// người ngoài không dò được token nào từng tồn tại.
public class PocShareLinkTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public PocShareLinkTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Create_IssuesUniqueTokenWithExpiry()
    {
        await using var db = NewDb();
        var first = await new CreatePocShareLinkUseCase(db).ExecuteAsync(_projectId, "gửi sếp", 7, "admin");
        var second = await new CreatePocShareLinkUseCase(db).ExecuteAsync(_projectId, null, 7, "admin");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Token, second!.Token);
        Assert.True(first.Token.Length >= 40);
        Assert.True(first.IsUsable);
        Assert.InRange(first.ExpiresAtUtc, DateTime.UtcNow.AddDays(6), DateTime.UtcNow.AddDays(8));
    }

    [Fact]
    public async Task Create_ClampsRequestedLifetime()
    {
        await using var db = NewDb();
        var tooLong = await new CreatePocShareLinkUseCase(db).ExecuteAsync(_projectId, null, days: 3650, "admin");
        var zero = await new CreatePocShareLinkUseCase(db).ExecuteAsync(_projectId, null, days: 0, "admin");

        Assert.True(tooLong!.ExpiresAtUtc <= DateTime.UtcNow.AddDays(CreatePocShareLinkUseCase.MaxDays + 1));
        Assert.True(zero!.ExpiresAtUtc > DateTime.UtcNow.AddDays(CreatePocShareLinkUseCase.DefaultDays - 1));
    }

    [Fact]
    public async Task Create_UnknownProject_ReturnsNull()
    {
        await using var db = NewDb();
        Assert.Null(await new CreatePocShareLinkUseCase(db).ExecuteAsync(Guid.NewGuid(), null, 7, "admin"));
    }

    [Fact]
    public async Task Resolve_ValidToken_ReturnsProject()
    {
        await using var db = NewDb();
        var link = await new CreatePocShareLinkUseCase(db).ExecuteAsync(_projectId, null, 7, "admin");

        Assert.Equal(_projectId, await new ResolvePocShareTokenQuery(NewDb()).ExecuteAsync(link!.Token));
    }

    [Fact]
    public async Task Resolve_RevokedOrExpiredOrUnknown_AllReturnNull()
    {
        await using var db = NewDb();
        var revoked = await new CreatePocShareLinkUseCase(db).ExecuteAsync(_projectId, null, 7, "admin");
        await new RevokePocShareLinkUseCase(db).ExecuteAsync(_projectId, revoked!.Id);

        var expired = await new CreatePocShareLinkUseCase(db).ExecuteAsync(_projectId, null, 7, "admin");
        await using (var edit = NewDb())
        {
            var entity = await edit.PocShareLinks.FirstAsync(x => x.Id == expired!.Id);
            entity.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await edit.SaveChangesAsync();
        }

        Assert.Null(await new ResolvePocShareTokenQuery(NewDb()).ExecuteAsync(revoked.Token));
        Assert.Null(await new ResolvePocShareTokenQuery(NewDb()).ExecuteAsync(expired!.Token));
        Assert.Null(await new ResolvePocShareTokenQuery(NewDb()).ExecuteAsync("khong-ton-tai"));
        Assert.Null(await new ResolvePocShareTokenQuery(NewDb()).ExecuteAsync(null));
    }

    [Fact]
    public async Task Revoke_FromAnotherProject_IsRefused()
    {
        await using var db = NewDb();
        var link = await new CreatePocShareLinkUseCase(db).ExecuteAsync(_projectId, null, 7, "admin");

        // Link phải thuộc đúng project mà caller đã được kiểm quyền — nếu không, biết id là thu hồi được
        // link của dự án người khác.
        Assert.False(await new RevokePocShareLinkUseCase(NewDb()).ExecuteAsync(Guid.NewGuid(), link!.Id));
        Assert.Equal(_projectId, await new ResolvePocShareTokenQuery(NewDb()).ExecuteAsync(link.Token));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
