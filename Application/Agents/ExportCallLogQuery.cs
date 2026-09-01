using ICOGenerator.Data;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

/// <summary>Một file Markdown đã dựng xong, chờ controller đẩy về trình duyệt.</summary>
public record CallLogExportFile(string FileName, string Markdown);

/// <summary>
/// Xuất MỘT lời gọi model ra file Markdown để mang đi hỏi chỗ khác — nút "Tải lời gọi này" ở màn Model
/// Invocation Detail.
///
/// <para>
/// Lưu ý về dữ liệu: <c>RequestJson</c>/<c>ResponseText</c> được mã hóa at-rest vì chúng chở lại toàn bộ
/// transcript của dự án (xem converter ở <c>AppDbContext</c>). Bản xuất này GIẢI MÃ trọn gói vào một file
/// rời, nên nó phải đi qua đúng cổng quyền của màn xem chi tiết (<c>ProjectResource.CallLog</c>) chứ không
/// được có đường tắt nào khác.
/// </para>
/// </summary>
public class ExportCallLogQuery
{
    private readonly AppDbContext _db;

    public ExportCallLogQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CallLogExportFile?> ExecuteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var log = await _db.AgentModelCallLogs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (log == null)
            return null;

        var item = ModelCallLogExportItem.From(log);
        return new CallLogExportFile(
            ModelCallLogMarkdown.FileName(item, cluster: false),
            ModelCallLogMarkdown.Render(item));
    }
}
