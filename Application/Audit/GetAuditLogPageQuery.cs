using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Audit;

/// <summary>
/// Đọc nhật ký thay đổi cấu hình, mới nhất trước, lọc tùy chọn theo <see cref="AuditCategory"/> và phân trang.
/// Chỉ đọc (AsNoTracking) — trang này không sửa gì.
/// </summary>
public class GetAuditLogPageQuery
{
    private readonly AppDbContext _db;
    public GetAuditLogPageQuery(AppDbContext db) => _db = db;

    public const int DefaultPageSize = 20;

    public async Task<AuditLogListPage> ExecuteAsync(
        AuditCategory? category = null, int page = 1, int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;

        var baseQuery = _db.AuditLogs.AsNoTracking();
        if (category is { } c)
            baseQuery = baseQuery.Where(x => x.Category == c);

        var ordered = baseQuery.OrderByDescending(x => x.CreatedAt);

        var totalCount = await ordered.CountAsync(cancellationToken);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new AuditLogListPage(items, category, page, pageSize, totalCount);
    }
}
