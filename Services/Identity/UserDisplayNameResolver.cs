using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Identity;

/// <summary>
/// Tra tên hiển thị (<see cref="Domain.AppUser.DisplayName"/>) từ Username để các bảng "Người tạo/
/// Người sửa" hiện tên người thay vì mã đăng nhập. Các dòng dữ liệu chỉ lưu Username (ví dụ
/// <c>Project.CreatedByUsername</c>), nên phần hiển thị nhờ resolver này map sang DisplayName.
/// Không tìm thấy user hoặc DisplayName trống ⇒ fallback về chính Username để không mất thông tin.
/// </summary>
public class UserDisplayNameResolver
{
    private readonly AppDbContext _db;

    public UserDisplayNameResolver(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Map một loạt Username → DisplayName trong đúng một truy vấn. Khoá không phân biệt hoa/thường.
    /// Username null/rỗng bị bỏ qua; user không có bản ghi hoặc DisplayName trống không xuất hiện trong
    /// dict ⇒ nơi gọi tự fallback về Username.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveManyAsync(
        IEnumerable<string?> usernames,
        CancellationToken cancellationToken = default)
    {
        var wanted = usernames
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wanted.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var users = await _db.AppUsers.AsNoTracking()
            .Where(u => wanted.Contains(u.Username) && u.DisplayName != null && u.DisplayName != "")
            .Select(u => new { u.Username, u.DisplayName })
            .ToListAsync(cancellationToken);

        return users
            .GroupBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Tên hiển thị cho một Username; fallback về chính Username khi không tra được.</summary>
    public string Resolve(IReadOnlyDictionary<string, string> map, string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return string.Empty;
        return map.TryGetValue(username.Trim(), out var displayName) ? displayName : username;
    }
}
