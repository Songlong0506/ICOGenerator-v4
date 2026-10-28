using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <param name="Files">Số file nguồn đã lưu được bảng cột. 0 ⇒ không lưu gì, UI báo lỗi và giữ bảng lại.</param>
public readonly record struct ConfirmColumnMapResult(int Files);

/// <summary>
/// SỬA bảng cột của các tài liệu nguồn dạng bảng tính. BA đã chốt và hệ thống đã lưu bảng ngay ở lượt đọc
/// file (<c>BAChatService.AcknowledgeSourcesAsync</c>); đây là đường người dùng mở "Xem / sửa bảng cột"
/// trong bong bóng của lượt đó, tích lại cột nào ứng dụng mới dùng, sửa cách hiểu, rồi lưu. Ghi đè
/// <see cref="ICOGenerator.Domain.ProjectSourceFile.ColumnMap"/> — từ đó <see cref="SourceContextBuilder"/>
/// và <see cref="RealSampleDataReader"/> đọc ra.
///
/// <para>
/// Use case này CHỈ lưu: không gọi LLM, không ghi lượt hội thoại nào, và trình duyệt cũng không gửi tin
/// nhắn nào theo sau. Bảng đã lưu được đính vào mọi lượt chat sau và vào lượt chắt lọc bản đồ bao phủ, nên
/// một câu "mình đã sửa bảng cột" chen vào hội thoại chỉ làm lạc nhịp câu hỏi đang chờ người dùng trả lời.
/// </para>
/// </summary>
public class ConfirmSourceColumnMapUseCase
{
    private readonly AppDbContext _db;

    public ConfirmSourceColumnMapUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Lưu bảng cột cho mọi file có mặt trong <paramref name="mapJson"/>. Trả về số file đã cập nhật (0 khi
    /// payload rỗng/hỏng, hoặc không dòng nào khớp cột thật của file — xem <see cref="SourceColumnMapBuilder"/>).
    /// </summary>
    public async Task<ConfirmColumnMapResult> ExecuteAsync(Guid projectId, string? mapJson, CancellationToken cancellationToken = default)
    {
        var submitted = SourceColumnMapBuilder.Parse(mapJson);
        if (submitted.Count == 0)
            return new ConfirmColumnMapResult(0);

        var byFile = submitted
            .GroupBy(n => n.FileName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var sources = await _db.ProjectSourceFiles
            .Where(s => s.ProjectId == projectId && s.Kind == SourceFileKind.Spreadsheet && s.ExtractedText != null)
            .ToListAsync(cancellationToken);

        var updated = 0;
        foreach (var source in sources)
        {
            // Payload chỉ có một file mà tên không khớp (trình duyệt gửi thiếu tên): chỉ nhận khi project có
            // đúng một bảng tính — ngoài ra thì im lặng gán nhầm file là chuyện tệ hơn không lưu gì.
            var notes = byFile.TryGetValue(source.FileName.Trim(), out var matched)
                ? matched
                : (sources.Count == 1 && byFile.Count == 1 ? byFile.Values.First() : null);
            if (notes == null)
                continue;

            // Server KHÔNG tin danh sách cột do client gửi, kể cả khi chính server vừa render nó ra vài giây
            // trước: chuẩn hoá lại theo hàng tiêu đề THẬT của file.
            var sanitized = SourceColumnMapBuilder.Sanitize(
                source.FileName, notes, SourceColumnMapBuilder.ReadColumnNames(source.ExtractedText));
            if (sanitized.Count == 0)
                continue;

            source.ColumnMap = JsonSerializer.Serialize(sanitized);
            updated++;
        }

        if (updated > 0)
            await _db.SaveChangesAsync(cancellationToken);

        return new ConfirmColumnMapResult(updated);
    }
}
