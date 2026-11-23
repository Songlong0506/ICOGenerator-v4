using ICOGenerator.Configuration;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ICOGenerator.Application.SystemLogs;

/// <summary>
/// Đọc log ứng dụng đã chép vào DB, mới nhất trước. Lọc tuỳ chọn theo mức tối thiểu (chọn Error ⇒ Error +
/// Fatal) và theo chuỗi con trong message / logger / exception / đường dẫn request. Chỉ đọc.
/// </summary>
public class GetSystemLogPageQuery
{
    public const int DefaultPageSize = 10;
    private const int MaxSearchLength = 200;

    private readonly AppDbContext _db;
    private readonly DatabaseLogSettings _settings;

    public GetSystemLogPageQuery(AppDbContext db, IOptions<DatabaseLogSettings> settings)
    {
        _db = db;
        _settings = settings.Value;
    }

    public async Task<SystemLogListPage> ExecuteAsync(
        SystemLogLevel? minimumLevel = null, string? search = null, int page = 1, int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (search is { Length: > MaxSearchLength })
            search = search[..MaxSearchLength];

        var query = _db.SystemLogs.AsNoTracking();
        if (minimumLevel is { } min)
        {
            // Level lưu dạng chuỗi nên không so sánh >= trong SQL được — đổi thành danh sách mức cụ thể.
            var levels = Enum.GetValues<SystemLogLevel>().Where(l => l >= min).ToList();
            query = query.Where(x => levels.Contains(x.Level));
        }
        if (search != null)
        {
            query = query.Where(x =>
                x.Message.Contains(search) ||
                (x.SourceContext != null && x.SourceContext.Contains(search)) ||
                (x.Exception != null && x.Exception.Contains(search)) ||
                (x.RequestPath != null && x.RequestPath.Contains(search)));
        }

        var ordered = query.OrderByDescending(x => x.Timestamp).ThenByDescending(x => x.Id);
        var totalCount = await ordered.CountAsync(cancellationToken);
        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new SystemLogListPage(items, minimumLevel, search, page, pageSize, totalCount,
            _settings.Enabled, _settings.MinimumLevel, _settings.RetentionDays);
    }
}
