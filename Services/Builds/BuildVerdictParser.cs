using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Builds;

/// <summary>Kết luận của cổng biên dịch cho một task Implementation/BuildFix.</summary>
public enum BuildVerdict
{
    /// <summary>
    /// Không tìm thấy dòng <c>BUILD:</c> — task chạy TRƯỚC khi có cổng này. Phải phân biệt với
    /// <see cref="Skipped"/>: gộp chúng lại là báo cáo chất lượng sẽ tính cả lịch sử cũ vào mẫu số
    /// và pha loãng đúng con số đang muốn theo dõi.
    /// </summary>
    Unknown,
    Pass,
    Fail,
    /// <summary>Không có gì để build (không dò ra dự án), hoặc bước cài phụ thuộc hỏng vì lý do hạ tầng.</summary>
    Skipped
}

/// <summary>
/// Dòng máy-đọc-được <c>BUILD: PASS|FAIL|SKIPPED</c> mà cổng biên dịch nối vào cuối
/// <c>AgentTask.Output</c>, và đường đọc lại nó.
/// <para>
/// Khác <see cref="Workflows.TestVerdictParser"/> ở một điểm quan trọng: dòng này do CHÍNH APP ghi
/// (kết quả của một tiến trình <c>dotnet build</c>/<c>npm run build</c> thật), không phải do model tự
/// khai. Nó tồn tại vì hai phía cần đọc lại kết quả ấy sau khi task kết thúc: bước Tech Lead review
/// nhận nó trong phần bàn giao, và trang Delivery Quality thống kê "tỷ lệ biên dịch được ngay lần đầu"
/// mà không phải dựng thêm bảng nào.
/// </para>
/// </summary>
public static class BuildVerdictParser
{
    /// <summary>Tiêu đề khối kết quả nối vào cuối output — mốc để người đọc nhận ra phần của cổng build.</summary>
    public const string ReportHeading = "## Kết quả cổng biên dịch";

    /// <summary>Tên file báo cáo build trong thư mục <c>04_Implementation</c> của workspace.</summary>
    public const string ReportFileName = "build-report.md";

    private static readonly Regex Marker = new(
        @"^\s*\*{0,2}BUILD\*{0,2}\s*[:=]\s*\*{0,2}\s*(PASS|FAIL|SKIPPED)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Dựng dòng kết luận để nối vào output — dùng chung một chỗ với đường đọc.</summary>
    public static string Format(BuildVerdict verdict) => $"BUILD: {verdict.ToString().ToUpperInvariant()}";

    /// <summary>
    /// Bỏ khối báo cáo mà cổng biên dịch đã nối vào một output, giữ lại phần BÀN GIAO do agent viết.
    /// <para>
    /// Dùng khi chở tóm tắt bàn giao của bước Implementation qua một vòng sửa lỗi biên dịch: chở kèm cả
    /// báo cáo cũ thì bước Tech Lead review đọc được một khối "BUILD: FAIL" đã lỗi thời ngay bên cạnh
    /// khối "BUILD: PASS" mới.
    /// </para>
    /// </summary>
    public static string StripReport(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return string.Empty;

        var index = output.IndexOf(ReportHeading, StringComparison.Ordinal);
        return (index < 0 ? output : output[..index]).TrimEnd();
    }

    public static BuildVerdict Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return BuildVerdict.Unknown;

        var matches = Marker.Matches(output);
        if (matches.Count == 0)
            return BuildVerdict.Unknown;

        // Lần xuất hiện CUỐI: output của một task BuildFix chở theo cả báo cáo vòng trước.
        return matches[^1].Groups[1].Value.ToUpperInvariant() switch
        {
            "PASS" => BuildVerdict.Pass,
            "FAIL" => BuildVerdict.Fail,
            _ => BuildVerdict.Skipped
        };
    }
}
