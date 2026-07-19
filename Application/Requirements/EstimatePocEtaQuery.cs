using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// Ước tính "POC sẽ sẵn sàng sau ~N phút" cho banner ngay sau khi user bấm Approve. N = trung bình
/// thời gian chạy THẬT (StartedAt→FinishedAt) của bước sinh AI Design Spec + bước dựng POC trên các
/// task đã hoàn tất của MỌI dự án — đo từ dữ liệu vận hành thay vì hằng số đoán mò. Chưa có lịch sử
/// (hệ thống mới) thì trả null và banner bỏ phần con số.
/// </summary>
public class EstimatePocEtaQuery
{
    private readonly AppDbContext _db;

    public EstimatePocEtaQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<int?> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Lấy về cặp mốc thời gian rồi tính phía client: Average trên TimeSpan không dịch được sang SQL
        // chung cho cả SqlServer lẫn Sqlite. Mỗi loại task chỉ vài chục dòng nên không đáng ngại.
        var durations = await _db.AgentTasks
            .AsNoTracking()
            .Where(t => t.Status == AgentTaskStatus.Completed
                        && t.StartedAt != null && t.FinishedAt != null
                        && (t.Type == AgentTaskType.AiDesignSpec || t.Type == AgentTaskType.PocPreview))
            .Select(t => new { t.Type, t.StartedAt, t.FinishedAt })
            .ToListAsync(cancellationToken);

        var perType = durations
            .GroupBy(t => t.Type)
            .Select(g => g.Average(t => (t.FinishedAt!.Value - t.StartedAt!.Value).TotalMinutes))
            .ToList();

        if (perType.Count == 0)
            return null;

        // Thiếu lịch sử một trong hai bước thì ước tính bằng phần đã có — vẫn hơn không có con số.
        return Math.Max(1, (int)Math.Ceiling(perType.Sum()));
    }
}
