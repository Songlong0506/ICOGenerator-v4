using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <param name="Files">Số file nguồn đã lưu được bảng cột. 0 ⇒ không lưu gì, UI giữ bảng lại.</param>
/// <param name="Message">
/// Tin nhắn người dùng mà trình duyệt gửi tiếp vào khung chat (do SERVER soạn — xem
/// <see cref="SourceColumnMapBuilder.RenderUserMessage"/>). Rỗng khi <paramref name="Files"/> = 0.
/// </param>
public readonly record struct ConfirmColumnMapResult(int Files, string Message);

/// <summary>
/// CHỐT bảng cột của các tài liệu nguồn dạng bảng tính: người dùng tích cột nào ứng dụng mới dùng, sửa lại
/// cách hiểu BA đề xuất, rồi gửi. Lưu vào <see cref="ICOGenerator.Domain.ProjectSourceFile.ColumnMap"/> —
/// từ đó <see cref="SourceContextBuilder"/> và <see cref="RealSampleDataReader"/> đọc ra.
///
/// <para>
/// Use case này CHỈ lưu, không gọi LLM và không ghi lượt hội thoại nào. Phần "kể lại cho BA nghe" đi đúng
/// đường chat thường: trình duyệt gửi tiếp <see cref="ConfirmColumnMapResult.Message"/> — do SERVER soạn từ
/// bảng đã chuẩn hoá (<see cref="SourceColumnMapBuilder.RenderUserMessage"/>) — như một tin nhắn người dùng
/// bình thường. Nhờ vậy không có đường ghi hội thoại thứ hai nào lệch khỏi luồng chính, và mọi thứ đã đúng ở
/// lượt chat (cổng readiness, chắt lọc bản đồ bao phủ, nhật ký điều đã chốt) tự khắc đúng ở đây. Chính tin
/// nhắn đó cũng là thứ báo cho lượt chat kế tiếp biết đây là lượt BA phải KỂ LẠI cách hiểu file theo bộ cột
/// vừa chốt (<see cref="SourceColumnMapBuilder.IsSubmissionMessage"/>).
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
    /// payload rỗng/hỏng, hoặc không dòng nào khớp cột thật của file — xem <see cref="SourceColumnMapBuilder"/>)
    /// kèm tin nhắn người dùng dựng từ ĐÚNG các dòng vừa lưu.
    /// </summary>
    public async Task<ConfirmColumnMapResult> ExecuteAsync(Guid projectId, string? mapJson, CancellationToken cancellationToken = default)
    {
        var submitted = SourceColumnMapBuilder.Parse(mapJson);
        if (submitted.Count == 0)
            return new ConfirmColumnMapResult(0, string.Empty);

        var byFile = submitted
            .GroupBy(n => n.FileName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var sources = await _db.ProjectSourceFiles
            .Where(s => s.ProjectId == projectId && s.Kind == SourceFileKind.Spreadsheet && s.ExtractedText != null)
            .ToListAsync(cancellationToken);

        var updated = 0;
        // Tin nhắn được soạn từ bản ĐÃ CHUẨN HOÁ của từng file (không phải payload gốc), nên nó kể đúng
        // những dòng vừa được ghi vào DB — kể cả khi vài dòng client gửi lên bị loại vì không khớp cột thật.
        var saved = new List<SourceColumnNote>();
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
            saved.AddRange(sanitized);
            updated++;
        }

        if (updated > 0)
            await _db.SaveChangesAsync(cancellationToken);

        return new ConfirmColumnMapResult(updated, SourceColumnMapBuilder.RenderUserMessage(saved));
    }
}
