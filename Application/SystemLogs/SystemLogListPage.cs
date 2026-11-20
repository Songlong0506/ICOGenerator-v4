using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.SystemLogs;

public record SystemLogListPage(
    IReadOnlyList<SystemLog> Items,
    SystemLogLevel? MinimumLevelFilter,
    string? Search,
    int Page,
    int PageSize,
    int TotalCount,
    // Cấu hình DatabaseLog đang bật hay không — tắt thì trang nói rõ vì sao không có dòng mới.
    bool CaptureEnabled,
    SystemLogLevel CaptureMinimumLevel,
    int RetentionDays)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasFilter => MinimumLevelFilter != null || !string.IsNullOrWhiteSpace(Search);
}
