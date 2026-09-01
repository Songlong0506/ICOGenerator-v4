using ICOGenerator.Data;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

/// <summary>
/// Xuất CẢ CỤM lời gọi quanh một lời gọi ra một file Markdown — nút "Tải cả cụm lượt".
///
/// <para>
/// Vì sao cần cụm chứ không phải một dòng: một thao tác của người dùng tốn vài lượt gọi model, và output
/// của lượt này là input của lượt kia (một lượt chat BA = bản đồ bao phủ + hồ sơ user + tóm tắt hội thoại →
/// lượt trả lời → chắt lọc hậu kỳ). Khi lượt trả lời sai, nguyên nhân thường nằm ở một lời gọi KHÁC trong
/// cùng cụm — tải mỗi dòng đang xem thì người gỡ lỗi thấy context xấu mà không thấy ai sinh ra nó.
/// </para>
///
/// <para>
/// <b>Cụm được SUY RA, không được lưu sẵn.</b> Không có cột định danh lượt trên <c>AgentModelCallLog</c>, nên
/// ranh giới cụm dựng từ ba ràng buộc đọc được: cùng dự án + cùng agent + cùng <c>WorkflowRunId</c> (kể cả
/// cùng null), và hai lời gọi liền nhau cách nhau không quá <see cref="GapSeconds"/> giây. Khoảng cách đo từ
/// lúc lời gọi trước KẾT THÚC tới lúc lời gọi sau BẮT ĐẦU (<c>CreatedAt - DurationMs</c>) chứ không phải
/// giữa hai mốc <c>CreatedAt</c>: các bước chuẩn bị chạy SONG SONG và một lời gọi dài 12 giây sẽ tự tạo ra
/// một "khoảng trống" 12 giây không có thật, đủ để cắt cụm ngay giữa lượt.
/// </para>
///
/// <para>
/// Ràng buộc <c>WorkflowRunId</c> + agent là thứ giữ cho cụm không nuốt việc của người khác: pipeline nền
/// chạy song song với khung chat, nên lọc theo thời gian đơn thuần sẽ trộn lời gọi của Developer vào giữa
/// một lượt chat BA. Phần suy ra vẫn có thể GỘP DƯ đuôi của lượt trước (người dùng bấm chip trả lời ngay khi
/// lượt chắt lọc hậu kỳ còn đang chạy) — dư thì người đọc bỏ qua được, còn thiếu thì họ không biết mà đi
/// tìm, nên ngưỡng cố ý nới về phía gộp dư. Trần <see cref="MaxCalls"/> chặn cỡ file khi một phiên bấm chip
/// liên tục làm các cụm dính vào nhau.
/// </para>
/// </summary>
public class ExportCallLogTurnQuery
{
    /// <summary>Khoảng nghỉ tối đa giữa hai lời gọi vẫn được coi là cùng một lượt làm việc.</summary>
    public const int GapSeconds = 30;

    /// <summary>Trần số lời gọi lấy về TRƯỚC lời gọi neo, và trần cho cả cụm.</summary>
    public const int MaxCallsBefore = 10;
    public const int MaxCalls = 20;

    /// <summary>Cửa sổ quét quanh lời gọi neo — cận trên thô để câu truy vấn không đọc cả bảng.</summary>
    private static readonly TimeSpan SearchWindow = TimeSpan.FromMinutes(30);

    private static readonly string GroupingNote =
        "Không có cột định danh lượt trên call log, nên cụm được suy ra từ: cùng dự án, cùng agent, cùng "
        + $"workflow run, và hai lời gọi liền nhau cách nhau không quá {GapSeconds} giây. Cụm vì thế "
        + "có thể gộp dư phần đuôi của lượt trước đó.";

    private readonly AppDbContext _db;

    public ExportCallLogTurnQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CallLogExportFile?> ExecuteAsync(Guid anchorId, CancellationToken cancellationToken = default)
    {
        var anchor = await _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => x.Id == anchorId)
            .Select(x => new { x.Id, x.ProjectId, x.AgentId, x.WorkflowRunId, x.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (anchor == null)
            return null;

        var from = anchor.CreatedAt - SearchWindow;
        var to = anchor.CreatedAt + SearchWindow;

        var scope = _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => x.ProjectId == anchor.ProjectId
                        && x.AgentId == anchor.AgentId
                        && x.CreatedAt >= from
                        && x.CreatedAt <= to);

        // So sánh với một biến null trong LINQ-to-Entities dịch ra "= NULL" (không bao giờ đúng) chứ không
        // phải "IS NULL", nên hai ca phải tách tường minh. Chat tương tác luôn rơi vào nhánh null.
        scope = anchor.WorkflowRunId == null
            ? scope.Where(x => x.WorkflowRunId == null)
            : scope.Where(x => x.WorkflowRunId == anchor.WorkflowRunId);

        // Quét bằng các cột NHẸ trước: RequestJson của một lượt chat dài tới hàng trăm KB và được giải mã
        // khi materialize, nên đọc cả cửa sổ 30 phút chỉ để tính ranh giới cụm là trả giá cho hàng chục lời
        // gọi rốt cuộc không nằm trong file.
        var timeline = await scope
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new CallSpan(x.Id, x.CreatedAt, x.DurationMs))
            .ToListAsync(cancellationToken);

        var ids = Cluster(timeline, anchor.Id);

        var logs = await _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var items = logs.Select(ModelCallLogExportItem.From).ToList();
        if (items.Count == 0)
            return null;

        var anchorItem = items.FirstOrDefault(x => x.Id == anchor.Id) ?? items[0];
        return new CallLogExportFile(
            ModelCallLogMarkdown.FileName(anchorItem, cluster: items.Count > 1),
            ModelCallLogMarkdown.Render(items, anchor.Id, GroupingNote));
    }

    /// <summary>Một lời gọi trên trục thời gian: mốc lưu log là lúc nó KẾT THÚC, nên lúc bắt đầu phải trừ ngược thời lượng.</summary>
    private sealed record CallSpan(Guid Id, DateTime EndUtc, long DurationMs)
    {
        public DateTime StartUtc => EndUtc.AddMilliseconds(-DurationMs);
    }

    /// <summary>
    /// Nới từ lời gọi neo ra hai phía chừng nào còn liền mạch. Lùi trước rồi mới tiến: phần TRƯỚC lời gọi
    /// neo là các bước đã nạp context cho nó — đó mới là chỗ trả lời câu hỏi "context xấu từ đâu ra".
    /// </summary>
    private static List<Guid> Cluster(List<CallSpan> timeline, Guid anchorId)
    {
        var anchor = timeline.FindIndex(x => x.Id == anchorId);
        if (anchor < 0)
            return new List<Guid> { anchorId };

        var gap = TimeSpan.FromSeconds(GapSeconds);
        var first = anchor;
        while (first > 0
               && anchor - first < MaxCallsBefore
               && timeline[first].StartUtc - timeline[first - 1].EndUtc <= gap)
            first--;

        var last = anchor;
        while (last < timeline.Count - 1
               && last - first + 1 < MaxCalls
               && timeline[last + 1].StartUtc - timeline[last].EndUtc <= gap)
            last++;

        return timeline.GetRange(first, last - first + 1).Select(x => x.Id).ToList();
    }
}
