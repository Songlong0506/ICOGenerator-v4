using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Cổng đối chiếu NEO CHỈ CHỖ: mỗi bước của mỗi kịch bản nghiệm thu phải có đúng một phần tử trong POC
/// mang <c>data-uat="{kịch bản}.{bước}"</c> (xem <see cref="UatAnchor"/>).
///
/// <para>
/// Vì sao là một cổng chứ không phải "có thì tốt": neo là thứ DUY NHẤT nối một câu tiếng Việt trong
/// checklist với một phần tử cụ thể trên bản demo. Không có nó, trang POC Review chỉ còn cách đoán theo
/// chữ — và đoán theo chữ thì "Kiểm tra JD được tạo với mã HcP-JD-XXX" khớp trúng một ô bảng bất kỳ có
/// chữ "JD", tức là chỉ sai chỗ một cách tự tin. Neo thiếu phải là ISSUE để agent gắn nốt trong chính
/// vòng audit đó (thêm một thuộc tính, rẻ nhất trong mọi loại issue), thay vì để người nghiệm thu phát
/// hiện lúc bấm.
/// </para>
///
/// <para>
/// Kiểm TĨNH thuần chuỗi: không cần Chromium, nên môi trường CI/máy dev không có browser vẫn giữ được
/// cổng này — giống tầng tĩnh của <see cref="PocUatCoverage"/>.
/// </para>
/// </summary>
public static class PocUatAnchors
{
    /// <summary>Số bước nêu đích danh trong một dòng issue — dài hơn thì cắt, agent không cần đọc hết mới hiểu.</summary>
    private const int MaxStepsPerIssue = 8;

    private static readonly Regex AnchorAttribute = new(
        UatAnchor.Attribute + @"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Mọi mã neo có trong HTML. Một phần tử phục vụ nhiều bước thì ghi nhiều mã cách nhau bằng dấu cách
    /// (<c>data-uat="1.4 3.2"</c>) — đúng ngữ nghĩa của bộ chọn CSS <c>[data-uat~="1.4"]</c> mà trang
    /// review và lượt lái dùng để tìm lại phần tử.
    /// </summary>
    public static IReadOnlyCollection<string> Collect(string? html)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(html))
            return tokens;

        foreach (Match m in AnchorAttribute.Matches(html))
        {
            foreach (var token in m.Groups["v"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                tokens.Add(token);
        }

        return tokens;
    }

    /// <summary>
    /// Đối chiếu bộ kịch bản với các neo có thật trong POC.
    /// </summary>
    /// <param name="uat">Bộ kịch bản nghiệm thu (rỗng ⇒ không kiểm gì).</param>
    /// <param name="contentBody">Vùng <c>POC_CONTENT</c> của poc-demo.html — phần do agent viết.</param>
    public static IReadOnlyList<string> Check(UatScenarioSet? uat, string? contentBody)
    {
        var scenarios = uat?.Scenarios ?? new List<UatScenario>();
        if (scenarios.Count == 0 || string.IsNullOrWhiteSpace(contentBody))
            return Array.Empty<string>();

        var present = Collect(contentBody);
        var issues = new List<string>();

        // Chưa gắn neo nào: nói MỘT lần cho cả bộ. Liệt kê từng bước thiếu lúc này là đổ vài chục dòng
        // issue cho một việc duy nhất "đi gắn thuộc tính đi", đẩy các issue thật ra khỏi tầm chú ý.
        if (present.Count == 0)
        {
            issues.Add(
                $"POC chưa gắn NEO CHỈ CHỖ nào ({UatAnchor.Attribute}) trong khi có {scenarios.Count} kịch bản nghiệm thu. "
                + "Người nghiệm thu bấm vào một bước trong checklist thì trang POC Review tô sáng đúng phần tử mang mã neo của bước đó; "
                + $"không có neo thì không chỉ được chỗ nào. Gắn {UatAnchor.Attribute}=\"{{số kịch bản}}.{{số bước}}\" (đánh số từ 1, theo đúng thứ tự kịch bản trong khối UAT của prompt) "
                + "lên phần tử của TỪNG bước: bước thao tác ⇒ chính nút/ô nhập/mục menu người dùng bấm; bước kiểm tra ⇒ phần tử hiển thị kết quả cần đối chiếu. "
                + $"Một phần tử phục vụ nhiều bước thì ghi nhiều mã cách nhau bằng dấu cách: {UatAnchor.Attribute}=\"1.4 3.2\".");
            return issues;
        }

        var expected = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < scenarios.Count; i++)
        {
            var scenario = scenarios[i];
            var missing = new List<string>();

            for (var j = 0; j < scenario.Steps.Count; j++)
            {
                var token = UatAnchor.Token(i, j);
                expected.Add(token);
                if (!present.Contains(token))
                    missing.Add($"{token} (\"{Shorten(scenario.Steps[j])}\")");
            }

            if (missing.Count == 0)
                continue;

            var listed = missing.Count > MaxStepsPerIssue
                ? string.Join("; ", missing.Take(MaxStepsPerIssue)) + $"; … và {missing.Count - MaxStepsPerIssue} bước nữa"
                : string.Join("; ", missing);

            issues.Add(
                $"Kịch bản nghiệm thu (UAT) '{scenario.Title}' còn {missing.Count}/{scenario.Steps.Count} bước chưa có neo chỉ chỗ: {listed}. "
                + $"Thêm {UatAnchor.Attribute}=\"<mã>\" vào phần tử mà bước đó đụng tới (bước kiểm tra thì trỏ vào chỗ HIỂN THỊ kết quả).");
        }

        // Neo trỏ vào một bước KHÔNG tồn tại: agent đánh số lệch (0-based, hoặc theo thứ tự khác với khối
        // UAT trong prompt). Không bắt thì cả kịch bản im lặng chỉ sai chỗ — đúng thứ cơ chế neo sinh ra
        // để loại bỏ.
        var stray = present.Where(t => !expected.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        if (stray.Count > 0)
        {
            var listed = stray.Count > MaxStepsPerIssue
                ? string.Join(", ", stray.Take(MaxStepsPerIssue)) + $", … ({stray.Count} mã)"
                : string.Join(", ", stray);
            issues.Add(
                $"Có neo chỉ chỗ không ứng với bước nào của bộ kịch bản: {listed}. "
                + $"Mã neo là \"{{số kịch bản}}.{{số bước}}\" đánh số TỪ 1 theo đúng thứ tự trong khối UAT của prompt "
                + $"(bộ này có {scenarios.Count} kịch bản, kịch bản 1 có {scenarios[0].Steps.Count} bước). Sửa lại cho khớp hoặc bỏ neo thừa.");
        }

        return issues;
    }

    private static string Shorten(string text)
    {
        var one = (text ?? string.Empty).Replace('\n', ' ').Trim();
        return one.Length <= 60 ? one : one[..59] + "…";
    }
}
