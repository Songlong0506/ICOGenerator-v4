using ICOGenerator.Application.Account;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Đăng nhập theo DB: băm/đối chiếu mật khẩu bằng PasswordHasher thật, trên AppDbContext Sqlite in-memory.
public class LoginUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IPasswordHasher<AppUser> _hasher = new PasswordHasher<AppUser>();

    public LoginUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task ValidCredentials_ReturnUserWithRole()
    {
        await SeedUser("teamdev", "Secret123", UserRole.TeamDev, isActive: true);
        var sut = new LoginUseCase(NewDb(), _hasher);

        var user = await sut.ExecuteAsync("teamdev", "Secret123");

        Assert.NotNull(user);
        Assert.Equal(UserRole.TeamDev, user!.Role);
    }

    [Fact]
    public async Task WrongPassword_ReturnsNull()
    {
        await SeedUser("admin", "Right", UserRole.Admin, isActive: true);
        var sut = new LoginUseCase(NewDb(), _hasher);

        Assert.Null(await sut.ExecuteAsync("admin", "Wrong"));
    }

    [Fact]
    public async Task InactiveUser_ReturnsNull_EvenWithRightPassword()
    {
        await SeedUser("user", "Secret123", UserRole.User, isActive: false);
        var sut = new LoginUseCase(NewDb(), _hasher);

        Assert.Null(await sut.ExecuteAsync("user", "Secret123"));
    }

    [Theory]
    [InlineData("ghost", "x")]   // user không tồn tại
    [InlineData(null, "x")]      // thiếu username
    [InlineData("user", "")]     // thiếu password
    public async Task MissingOrUnknown_ReturnsNull(string? username, string password)
    {
        await SeedUser("user", "Secret123", UserRole.User, isActive: true);
        var sut = new LoginUseCase(NewDb(), _hasher);

        Assert.Null(await sut.ExecuteAsync(username, password));
    }

    private async Task SeedUser(string username, string password, UserRole role, bool isActive)
    {
        await using var db = NewDb();
        var user = new AppUser { Username = username, Role = role, IsActive = isActive, DisplayName = username };
        user.PasswordHash = _hasher.HashPassword(user, password);
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
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
