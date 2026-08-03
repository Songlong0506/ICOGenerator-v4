using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>
/// Một người trong danh bạ nhân sự, rút gọn đúng mức cần để NHẬN RA người nhận link: tên, email và nơi
/// làm việc. Không kèm dữ liệu cá nhân nhạy cảm (ngày sinh, điện thoại, địa chỉ đón) — ô gợi ý này chỉ
/// giúp gõ đúng tên, không phải cửa sổ tra cứu hồ sơ nhân sự.
/// </summary>
public record AssociateSuggestion(string DisplayName, string? Email, string? OrganizationUnit, string? Position);

/// <summary>
/// Gợi ý người nhận cho ô "Gửi cho ai" của hộp thoại chia sẻ bản demo, lấy từ bảng <c>Associates</c>
/// (đồng bộ từ HR_Portal). Trước đây ô này là text tự do nên nhãn link viết mỗi lần một kiểu
/// ("sếp Hào", "a.Hao", "Hao PS/EPC2") — cùng một người mà nhìn danh sách link không nhận ra.
///
/// Người đã nghỉ (<c>LeavingDate</c>) và bản ghi đã xóa mềm bị loại: gửi bản demo cho họ là vô nghĩa.
/// </summary>
public class SearchAssociatesQuery
{
    /// <summary>Số gợi ý trả về. Đủ để chọn bằng mắt, ít đến mức không phải cuộn trong hộp thoại.</summary>
    public const int MaxResults = 8;

    /// <summary>Dưới ngưỡng này thì mọi từ khóa đều khớp cả nghìn người — không phải gợi ý nữa.</summary>
    public const int MinKeywordLength = 2;

    private readonly AppDbContext _db;

    public SearchAssociatesQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<AssociateSuggestion>> ExecuteAsync(string? keyword, CancellationToken cancellationToken = default)
    {
        var key = keyword?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key) || key.Length < MinKeywordLength)
            return [];

        // So khớp trên bản thường hóa chữ thường để kết quả không phụ thuộc collation của DB (SQL Server
        // mặc định không phân biệt hoa/thường, SQLite trong test thì có). Bảng chỉ vài nghìn dòng nên
        // việc quét không dùng index ở đây không đáng kể.
        return await _db.Associates.AsNoTracking()
            .Where(a => !a.IsDelete && a.LeavingDate == null && a.DisplayName != null)
            .Where(a => a.DisplayName!.ToLower().Contains(key)
                        || (a.Email != null && a.Email.ToLower().Contains(key))
                        || (a.UserId != null && a.UserId.ToLower().Contains(key)))
            // Khớp từ ĐẦU tên lên trước: gõ "le" thì "Le Van Trung" hữu ích hơn "Nguyen Thi Le".
            .OrderByDescending(a => a.DisplayName!.ToLower().StartsWith(key))
            .ThenBy(a => a.DisplayName)
            .Take(MaxResults)
            .Select(a => new AssociateSuggestion(a.DisplayName!, a.Email, a.OrganizationUnit, a.Position))
            .ToListAsync(cancellationToken);
    }
}
