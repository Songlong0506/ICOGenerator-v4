using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Account;

/// <summary>
/// Xác thực người dùng theo bảng AppUser (thay cho credential dùng chung trong config trước đây):
/// tìm user đang hoạt động theo username rồi kiểm tra mật khẩu băm bằng PasswordHasher. Trả về
/// AppUser khi hợp lệ (để controller đọc Role và phát hành claim), hoặc null khi sai.
/// Bộ user được seed trong DbInitializer với mật khẩu mặc định (đổi sau lần đăng nhập đầu).
/// </summary>
public class LoginUseCase
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<AppUser> _passwordHasher;

    public LoginUseCase(AppDbContext db, IPasswordHasher<AppUser> passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public async Task<AppUser?> ExecuteAsync(string? username, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return null;

        var user = await _db.AppUsers
            .FirstOrDefaultAsync(x => x.Username == username && x.IsActive, cancellationToken);
        if (user is null)
            return null;

        var result = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : user;
    }
}
