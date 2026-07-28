using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Cổng đối chiếu POC với bộ KỊCH BẢN NGHIỆM THU (UAT) đã sinh TRƯỚC khi dựng POC.
///
/// <para>
/// Vì sao cần: các assertion runtime của POC (<c>window.pocSelfTest</c>, <c>window.pocScenarios</c>) đều do
/// CHÍNH agent dựng POC viết ra — cùng một tác giả, cùng một cách hiểu. Nó pass đúng những gì nó đã hiểu,
/// kể cả khi hiểu sai. Bộ UAT thì được BA sinh từ AI Design Spec TRƯỚC khi Developer đặt bút (xem
/// <see cref="Services.Requirements.UatScenarioService"/>) và cũng chính là checklist con người sẽ nghiệm
/// thu trên trang POC Review — nên nó là ĐÍCH ĐỘC LẬP: mỗi kịch bản UAT phải có một mục tương ứng trong
/// <c>pocScenarios()</c> và mục đó phải PASS. Thiếu một kịch bản = POC chưa demo được đường đi mà người
/// dùng sẽ nghiệm thu, dù audit tĩnh có sạch.
/// </para>
/// </summary>
public static class PocUatCoverage
{
    /// <summary>Số ký tự tối thiểu để cho phép so khớp kiểu "chứa nhau" — nhãn quá ngắn dễ khớp nhầm.</summary>
    private const int MinContainmentLength = 8;

    /// <summary>Tỷ lệ từ chung tối thiểu để coi hai tiêu đề là cùng một kịch bản.</summary>
    private const double MinTokenOverlap = 0.6;

    /// <summary>
    /// Đối chiếu bộ UAT với kết quả <c>pocScenarios()</c> mà runtime vừa chạy.
    /// </summary>
    /// <param name="uat">Bộ kịch bản nghiệm thu sinh từ spec (rỗng ⇒ không kiểm gì, giữ nguyên hành vi cũ).</param>
    /// <param name="runtimeRan">Runtime (headless browser) có chạy không — không chạy thì chỉ kiểm được phần tĩnh.</param>
    /// <param name="scenarioResults">Các dòng "PASS/FAIL &lt;tên&gt; — chi tiết" của <c>pocScenarios()</c>.</param>
    /// <param name="scriptBody">Nội dung vùng script của POC — để bắt trường hợp chưa định nghĩa hàm.</param>
    public static IReadOnlyList<string> Check(
        UatScenarioSet? uat,
        bool runtimeRan,
        IReadOnlyList<string> scenarioResults,
        string? scriptBody)
    {
        var scenarios = uat?.Scenarios ?? new List<UatScenario>();
        if (scenarios.Count == 0)
            return Array.Empty<string>();

        var issues = new List<string>();

        // Tầng TĨNH: không cần browser vẫn bắt được "chưa viết hàm nào" — môi trường không có Chromium
        // (CI, máy dev) vẫn giữ được cổng này thay vì mất trắng.
        if (!string.IsNullOrWhiteSpace(scriptBody) && !scriptBody.Contains("pocScenarios", StringComparison.Ordinal))
        {
            issues.Add(
                $"POC script chưa định nghĩa window.pocScenarios() trong khi đã có {scenarios.Count} kịch bản nghiệm thu (UAT) phải demo được: "
                + $"{FormatTitles(scenarios)}. Định nghĩa hàm GLOBAL trả về MỖI kịch bản một phần tử "
                + "{ title: '<đúng tiêu đề kịch bản UAT>', pass: <boolean>, detail: '<kỳ vọng vs thực tế>' }, "
                + "pass tính bằng cách GỌI chính các hàm nghiệp vụ/điều hướng của POC theo đúng trình tự các bước của kịch bản.");
            return issues;
        }

        // Không có runtime thì không biết kịch bản nào đã chạy — dừng ở tầng tĩnh, không đoán bừa.
        if (!runtimeRan)
            return issues;

        foreach (var scenario in scenarios)
        {
            var match = FindMatch(scenario.Title, scenarioResults);
            if (match == null)
            {
                issues.Add(
                    $"Kịch bản nghiệm thu (UAT) '{scenario.Title}' KHÔNG có mục tương ứng trong window.pocScenarios() — "
                    + $"đây là kịch bản người dùng sẽ nghiệm thu trên POC{FormatScreen(scenario)}. "
                    + $"Bổ sung một phần tử {{ title: '{scenario.Title}', pass: …, detail: … }} mô phỏng đúng các bước: {FormatSteps(scenario)}");
                continue;
            }

            // Có mục nhưng FAIL: runtime đã báo issue riêng cho nó rồi, ở đây chỉ nói rõ mục đó GẮN với
            // kịch bản nghiệm thu nào — người review và agent thấy được "cái đang gãy chính là điều sẽ bị
            // nghiệm thu", thay vì một dòng FAIL trôi giữa các dòng khác.
            if (match.StartsWith("FAIL", StringComparison.Ordinal))
                issues.Add($"Kịch bản nghiệm thu (UAT) '{scenario.Title}' đang FAIL trong window.pocScenarios(): {match[4..].Trim()}. Sửa LOGIC cho luồng này chạy đúng — đây là điều người dùng sẽ bấm thử khi nghiệm thu.");
        }

        return issues;
    }

    /// <summary>Số kịch bản UAT đã có mục PASS tương ứng — dùng cho dòng tóm tắt trên trang review.</summary>
    public static int CountPassing(UatScenarioSet? uat, IReadOnlyList<string> scenarioResults)
    {
        var scenarios = uat?.Scenarios ?? new List<UatScenario>();
        return scenarios.Count(s => FindMatch(s.Title, scenarioResults) is { } m && m.StartsWith("PASS", StringComparison.Ordinal));
    }

    // Tìm dòng kết quả runtime ứng với một tiêu đề UAT. Agent được yêu cầu dùng NGUYÊN VĂN tiêu đề, nhưng
    // model hay thêm/bớt vài chữ — nên khớp theo 3 mức nới dần: bằng nhau → chứa nhau → đủ từ chung.
    private static string? FindMatch(string title, IReadOnlyList<string> scenarioResults)
    {
        if (string.IsNullOrWhiteSpace(title) || scenarioResults.Count == 0)
            return null;

        var target = Normalize(title);
        if (target.Length == 0)
            return null;

        string? best = null;
        var bestScore = 0d;

        foreach (var line in scenarioResults)
        {
            var name = Normalize(ExtractName(line));
            if (name.Length == 0)
                continue;

            if (name == target)
                return line;

            var shorter = name.Length <= target.Length ? name : target;
            if (shorter.Length >= MinContainmentLength
                && (name.Contains(target, StringComparison.Ordinal) || target.Contains(name, StringComparison.Ordinal)))
                return line;

            var score = TokenOverlap(target, name);
            if (score > bestScore)
            {
                bestScore = score;
                best = line;
            }
        }

        return bestScore >= MinTokenOverlap ? best : null;
    }

    // "PASS <tên> — chi tiết" → "<tên>". Dấu "—" là ranh giới do PocRuntimeChecker đặt khi format.
    private static string ExtractName(string resultLine)
    {
        var text = resultLine ?? string.Empty;
        if (text.StartsWith("PASS ", StringComparison.Ordinal) || text.StartsWith("FAIL ", StringComparison.Ordinal))
            text = text[5..];
        var dash = text.IndexOf(" — ", StringComparison.Ordinal);
        return dash >= 0 ? text[..dash] : text;
    }

    private static double TokenOverlap(string a, string b)
    {
        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 2).ToHashSet(StringComparer.Ordinal);
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 2).ToHashSet(StringComparer.Ordinal);
        if (tokensA.Count == 0 || tokensB.Count == 0)
            return 0;
        var common = tokensA.Count(t => tokensB.Contains(t));
        return (double)common / Math.Min(tokensA.Count, tokensB.Count);
    }

    // Bỏ dấu câu + gộp khoảng trắng + thường hoá: hai tiêu đề chỉ khác dấu phẩy/nháy vẫn là một.
    private static string Normalize(string text)
    {
        var cleaned = PunctuationRegex.Replace((text ?? string.Empty).ToLowerInvariant(), " ");
        return WhitespaceRegex.Replace(cleaned, " ").Trim();
    }

    private static readonly Regex PunctuationRegex = new(@"[^\p{L}\p{N}\s]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static string FormatTitles(IReadOnlyList<UatScenario> scenarios) =>
        string.Join("; ", scenarios.Select(s => $"'{s.Title}'"));

    private static string FormatScreen(UatScenario scenario) =>
        string.IsNullOrWhiteSpace(scenario.Screen) ? string.Empty : $" (màn hình '{scenario.Screen}')";

    private static string FormatSteps(UatScenario scenario) =>
        scenario.Steps.Count == 0 ? "(không có bước)" : string.Join(" → ", scenario.Steps);
}
