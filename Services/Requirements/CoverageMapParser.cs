using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Đọc text "Bản đồ bao phủ yêu cầu" (12 dòng bullet do <see cref="RequirementCoverageService"/> duy trì,
/// format ghim trong <c>Prompts/BusinessAnalyst/requirement-coverage.v1.md</c>) thành danh sách
/// <see cref="CoverageMapItem"/> cho UI render panel tiến độ cạnh khung chat. Trước đây bản đồ chỉ dành
/// cho BA/gate — user không biết cuộc phỏng vấn còn bao nhiêu nhóm chưa rõ; panel này là lý do parser
/// tồn tại. Chịu lỗi: dòng không đúng dạng thì bỏ qua (model lỡ chèn lời dẫn), map rỗng → danh sách rỗng.
/// </summary>
public static partial class CoverageMapParser
{
    public static IReadOnlyList<CoverageMapItem> Parse(string? coverageMap)
    {
        if (string.IsNullOrWhiteSpace(coverageMap))
            return Array.Empty<CoverageMapItem>();

        var items = new List<CoverageMapItem>();
        foreach (var raw in coverageMap.Replace("\r\n", "\n").Split('\n'))
        {
            var match = CoverageLineRegex().Match(raw.Trim());
            if (!match.Success)
                continue;

            items.Add(new CoverageMapItem
            {
                IsCore = match.Groups["core"].Success,
                Label = match.Groups["label"].Value.Trim(),
                Status = NormalizeStatus(match.Groups["status"].Value),
                Summary = match.Groups["summary"].Value.Trim()
            });
        }

        return items;
    }

    /// <summary>Số nhóm ÁP DỤNG đã [RÕ] và tổng số nhóm áp dụng (bỏ [KHÔNG ÁP DỤNG]) — cho dòng "đã rõ x/y".</summary>
    public static (int Clear, int Applicable) Progress(IReadOnlyList<CoverageMapItem> items)
    {
        var applicable = items.Count(x => x.Status != "KHÔNG ÁP DỤNG");
        var clear = items.Count(x => x.Status == "RÕ");
        return (clear, applicable);
    }

    private static string NormalizeStatus(string raw)
    {
        var status = raw.Trim().ToUpperInvariant();
        return status switch
        {
            "RÕ" or "RO" => "RÕ",
            "MỘT PHẦN" or "MOT PHAN" => "MỘT PHẦN",
            "KHÔNG ÁP DỤNG" or "KHONG AP DUNG" => "KHÔNG ÁP DỤNG",
            _ => "CHƯA HỎI"
        };
    }

    // "- ★ Mục tiêu / bài toán: [RÕ] tóm tắt…" — ★ tùy chọn, nhãn tới dấu ':' cuối cùng trước '[',
    // trạng thái trong ngoặc vuông, phần còn lại là tóm tắt.
    [GeneratedRegex(@"^-\s*(?<core>★)?\s*(?<label>[^:\[\]]+):\s*\[(?<status>[^\]]+)\]\s*(?<summary>.*)$")]
    private static partial Regex CoverageLineRegex();
}
