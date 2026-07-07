using System.Text.Json;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Quality;

/// <summary>
/// Parse output của model thành <see cref="TraceabilityMatrix"/>. Khoan dung như các parser khác trong
/// app: chấp nhận code-fence/văn dẫn quanh JSON (JsonExtractor), thiếu mảng nào coi như rỗng, status lạ
/// được ép về "partial" (đáng ngờ thì báo "một phần" thay vì tô xanh/đỏ sai). Chỉ fail khi không rút được
/// JSON hoặc không có dòng yêu cầu hợp lệ nào — ma trận rỗng là vô nghĩa, thà bắt người dùng chạy lại.
/// </summary>
public static class TraceabilityMatrixParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static bool TryParse(string content, out TraceabilityMatrix? matrix)
    {
        matrix = null;

        var json = JsonExtractor.Extract(content);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        RawMatrix? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawMatrix>(json, Options);
        }
        catch (JsonException)
        {
            return false;
        }
        if (raw?.Requirements == null)
            return false;

        var requirements = new List<TraceabilityRequirement>();
        for (var i = 0; i < raw.Requirements.Count; i++)
        {
            var row = raw.Requirements[i];
            if (row == null || string.IsNullOrWhiteSpace(row.Title))
                continue;

            requirements.Add(new TraceabilityRequirement(
                string.IsNullOrWhiteSpace(row.Code) ? $"R-{i + 1:00}" : row.Code.Trim(),
                row.Title.Trim(),
                string.IsNullOrWhiteSpace(row.Kind) ? null : row.Kind.Trim(),
                Clean(row.Stories),
                Clean(row.CodeFiles),
                Clean(row.Tests),
                NormalizeStatus(row.Status),
                string.IsNullOrWhiteSpace(row.Note) ? null : row.Note.Trim()));
        }
        if (requirements.Count == 0)
            return false;

        var orphans = (raw.OrphanStories ?? [])
            .Where(o => o != null && !string.IsNullOrWhiteSpace(o.Story))
            .Select(o => new TraceabilityOrphanStory(o!.Story!.Trim(),
                string.IsNullOrWhiteSpace(o.Reason) ? null : o.Reason.Trim()))
            .ToList();

        matrix = new TraceabilityMatrix(
            requirements,
            orphans,
            string.IsNullOrWhiteSpace(raw.Summary) ? null : raw.Summary.Trim());
        return true;
    }

    /// <summary>Serialize ma trận về JSON chuẩn hoá (camelCase) để lưu vào ProjectTraceability.MatrixJson.</summary>
    public static string Serialize(TraceabilityMatrix matrix) => JsonSerializer.Serialize(matrix, Options);

    /// <summary>Đọc lại ma trận đã lưu; null khi JSON hỏng (dữ liệu tay/di sản) — UI coi như chưa phân tích.</summary>
    public static TraceabilityMatrix? Deserialize(string matrixJson)
    {
        try
        {
            return JsonSerializer.Deserialize<TraceabilityMatrix>(matrixJson, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Clean(List<string?>? values) =>
        (values ?? [])
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v!.Trim())
        .ToList();

    private static string NormalizeStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        TraceabilityMatrix.StatusCovered => TraceabilityMatrix.StatusCovered,
        TraceabilityMatrix.StatusMissing => TraceabilityMatrix.StatusMissing,
        _ => TraceabilityMatrix.StatusPartial
    };

    // Hình dạng thô, mọi trường nullable — model trả thiếu trường nào cũng không nổ deserialize.
    private sealed class RawMatrix
    {
        public List<RawRequirement?>? Requirements { get; set; }
        public List<RawOrphan?>? OrphanStories { get; set; }
        public string? Summary { get; set; }
    }

    private sealed class RawRequirement
    {
        public string? Code { get; set; }
        public string? Title { get; set; }
        public string? Kind { get; set; }
        public List<string?>? Stories { get; set; }
        public List<string?>? CodeFiles { get; set; }
        public List<string?>? Tests { get; set; }
        public string? Status { get; set; }
        public string? Note { get; set; }
    }

    private sealed class RawOrphan
    {
        public string? Story { get; set; }
        public string? Reason { get; set; }
    }
}
