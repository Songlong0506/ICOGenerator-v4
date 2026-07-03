using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

// Biến raw text của vòng tự soát thành danh sách vấn đề. Reviewer được nhắc trả JSON
// {"issues": ["...", ...]}. Model local không phải lúc nào cũng tuân thủ, nên parser fail-open: không
// đọc được JSON thì coi như KHÔNG có vấn đề (bản nháp đạt) để vòng tự soát không chặn việc sinh tài liệu.
public class ProductBriefReviewParser
{
    // Giữ vòng sửa tập trung: quá nhiều vấn đề một lúc khiến bản sửa dễ hỏng chỗ khác; và bỏ "vấn đề"
    // quá dài (model lỡ nhét cả đoạn phân tích).
    private const int MaxIssues = 8;
    private const int MaxIssueLength = 500;

    public ProductBriefReview Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
            return ProductBriefReview.PassDefault;

        try
        {
            var json = JsonExtractor.Extract(text);
            if (string.IsNullOrEmpty(json))
                return ProductBriefReview.PassDefault;

            var parsed = JsonSerializer.Deserialize<RawReview>(json, JsonDefaults.CaseInsensitive);
            return parsed == null ? ProductBriefReview.PassDefault : Clean(parsed);
        }
        catch
        {
            // JSON hỏng → fail-open: coi như bản nháp đạt.
            return ProductBriefReview.PassDefault;
        }
    }

    /// <summary>Chuẩn hoá kết quả structured output về cùng giới hạn (số lượng/độ dài/trùng lặp) với đường parse text.</summary>
    public ProductBriefReview Normalize(ProductBriefReview review) =>
        Clean(new RawReview { Issues = review.Issues });

    private static ProductBriefReview Clean(RawReview raw)
    {
        var result = new ProductBriefReview();
        if (raw.Issues == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in raw.Issues)
        {
            var value = (issue ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > MaxIssueLength || !seen.Add(value))
                continue;

            result.Issues.Add(value);
            if (result.Issues.Count >= MaxIssues)
                break;
        }

        return result;
    }

    private class RawReview
    {
        public List<string>? Issues { get; set; }
    }
}
