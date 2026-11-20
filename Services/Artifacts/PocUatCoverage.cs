using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Cổng đối chiếu POC với bộ KỊCH BẢN NGHIỆM THU (UAT) đã sinh TRƯỚC khi dựng POC — và là nơi DUY NHẤT
/// biến kết quả kịch bản của runtime (<c>window.pocScenarios()</c> + lượt lái thật bằng click) thành issue.
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
/// <para>
/// MỘT kịch bản gãy = MỘT issue. Một kịch bản UAT hỏng thường hỏng ở cả ba tầng cùng lúc (không có mục /
/// mục FAIL trong <c>pocScenarios()</c>, và lượt bấm thử cũng không thao tác được). Trước đây mỗi tầng tự
/// sinh issue riêng nên một chỗ gãy thành 2–3 "điểm chưa đạt" — con số trên POC Review và bảng so sánh
/// biến thể phồng lên, và hai bản demo gãy cùng một kịch bản có thể lệch nhau 2 điểm chỉ vì một bản còn
/// bấm được nút. Nay các tầng chỉ trả SỰ KIỆN (<see cref="PocRuntimeReport.ScenarioResults"/>,
/// <see cref="PocRuntimeReport.UatDriveResults"/>), lớp này gộp theo kịch bản rồi mới kết luận.
/// </para>
/// </summary>
public static class PocUatCoverage
{
    /// <summary>Số ký tự tối thiểu để cho phép so khớp kiểu "chứa nhau" — nhãn quá ngắn dễ khớp nhầm.</summary>
    private const int MinContainmentLength = TextSimilarity.DefaultMinContainmentLength;

    /// <summary>Tỷ lệ từ chung tối thiểu để coi hai tiêu đề là cùng một kịch bản.</summary>
    private const double MinTokenOverlap = TextSimilarity.DefaultMinTokenOverlap;

    /// <summary>
    /// Mọi issue về kịch bản của một vòng audit: mỗi kịch bản UAT chưa đạt đúng MỘT issue (gộp mọi tầng
    /// gãy của nó), cộng mỗi luồng end-to-end NGOÀI bộ UAT đang FAIL một issue.
    /// </summary>
    /// <param name="uat">Bộ kịch bản nghiệm thu sinh từ spec (rỗng ⇒ chỉ còn xét luồng end-to-end agent tự đặt).</param>
    /// <param name="runtimeRan">Runtime (headless browser) có chạy không — không chạy thì chỉ kiểm được phần tĩnh.</param>
    /// <param name="scenarioResults">Các dòng "PASS/FAIL &lt;tên&gt; — chi tiết" của <c>pocScenarios()</c>.</param>
    /// <param name="driveResults">Kết quả lái thật bằng click — phần tử thứ i ứng với kịch bản UAT thứ i.</param>
    /// <param name="scriptBody">Nội dung vùng script của POC — để bắt trường hợp chưa định nghĩa hàm.</param>
    public static IReadOnlyList<string> Check(
        UatScenarioSet? uat,
        bool runtimeRan,
        IReadOnlyList<string> scenarioResults,
        IReadOnlyList<PocUatDriveResult> driveResults,
        string? scriptBody)
    {
        var (uatIssues, outsideIssues) = Evaluate(uat, runtimeRan, scenarioResults, driveResults, scriptBody);
        return uatIssues.Concat(outsideIssues).ToList();
    }

    /// <summary>
    /// CHỈ các issue của bộ kịch bản nghiệm thu (UAT) — phần của <see cref="Check"/> trừ đi các luồng
    /// end-to-end agent tự đặt thêm. Đây là thước của cổng kịch bản nghiệm thu ở worker
    /// (<see cref="Services.Workflows.PocUatGate"/>): bộ UAT là thứ người dùng sẽ bấm thử, còn luồng
    /// ngoài UAT do chính agent đặt ra và người dùng không bao giờ thấy.
    /// </summary>
    public static IReadOnlyList<string> CheckUat(
        UatScenarioSet? uat,
        bool runtimeRan,
        IReadOnlyList<string> scenarioResults,
        IReadOnlyList<PocUatDriveResult> driveResults,
        string? scriptBody) =>
        Evaluate(uat, runtimeRan, scenarioResults, driveResults, scriptBody).UatIssues;

    private static (List<string> UatIssues, List<string> OutsideIssues) Evaluate(
        UatScenarioSet? uat,
        bool runtimeRan,
        IReadOnlyList<string> scenarioResults,
        IReadOnlyList<PocUatDriveResult> driveResults,
        string? scriptBody)
    {
        var scenarios = uat?.Scenarios ?? new List<UatScenario>();
        var issues = new List<string>();
        var outside = new List<string>();

        // Tầng TĨNH: không cần browser vẫn bắt được "chưa viết hàm nào" — môi trường không có Chromium
        // (CI, máy dev) vẫn giữ được cổng này thay vì mất trắng. Một issue cho cả bộ thay vì N issue "thiếu
        // mục" — cùng một nguyên nhân.
        var noScenarioFunction = scenarios.Count > 0
            && !string.IsNullOrWhiteSpace(scriptBody)
            && !scriptBody.Contains("pocScenarios", StringComparison.Ordinal);
        if (noScenarioFunction)
        {
            issues.Add(
                $"POC script chưa định nghĩa window.pocScenarios() trong khi đã có {scenarios.Count} kịch bản nghiệm thu (UAT) phải demo được: "
                + $"{FormatTitles(scenarios)}. Định nghĩa hàm GLOBAL trả về MỖI kịch bản một phần tử "
                + "{ title: '<đúng tiêu đề kịch bản UAT>', pass: <boolean>, detail: '<kỳ vọng vs thực tế>' }, "
                + "pass tính bằng cách GỌI chính các hàm nghiệp vụ/điều hướng của POC theo đúng trình tự các bước của kịch bản.");
        }

        // Không có runtime thì không biết kịch bản nào đã chạy — dừng ở tầng tĩnh, không đoán bừa.
        if (!runtimeRan)
            return (issues, outside);

        var claimed = new HashSet<int>();
        for (var index = 0; index < scenarios.Count; index++)
        {
            var scenario = scenarios[index];
            var matchIndex = FindMatchIndex(scenario.Title, scenarioResults);
            if (matchIndex >= 0)
                claimed.Add(matchIndex);

            var problems = new List<string>();
            if (matchIndex < 0)
            {
                // Hàm chưa có thì issue tĩnh ở trên đã nói thay cho cả bộ.
                if (!noScenarioFunction)
                    problems.Add(
                        $"window.pocScenarios() KHÔNG có mục tương ứng — bổ sung một phần tử {{ title: '{scenario.Title}', pass: …, detail: … }} "
                        + $"mô phỏng đúng các bước: {FormatSteps(scenario)}");
            }
            else if (scenarioResults[matchIndex].StartsWith("FAIL", StringComparison.Ordinal))
            {
                problems.Add(
                    $"window.pocScenarios() báo FAIL: {scenarioResults[matchIndex][4..].Trim()} — sửa LOGIC cho luồng chạy đúng, đừng sửa assertion cho pass giả");
            }

            if (DriveResultFor(index, scenario, driveResults) is { Pass: false } drive)
                problems.Add(
                    $"lái thật bằng click KHÔNG thao tác được — {drive.Detail}; sửa cho đúng nhãn/điều khiển và nối logic thật cho nút đó");

            if (problems.Count > 0)
                issues.Add(
                    $"Kịch bản nghiệm thu (UAT) '{scenario.Title}' chưa đạt{FormatScreen(scenario)} — đây là điều người dùng sẽ bấm thử khi nghiệm thu: "
                    + string.Join("; VÀ ", problems) + ".");
        }

        // Luồng end-to-end agent tự đặt NGOÀI bộ UAT: không gắn với kịch bản nào nên đứng riêng một issue.
        for (var i = 0; i < scenarioResults.Count; i++)
        {
            var line = scenarioResults[i];
            if (claimed.Contains(i) || !line.StartsWith("FAIL", StringComparison.Ordinal))
                continue;
            var text = line[4..].Trim();
            outside.Add($"Kịch bản nghiệp vụ (end-to-end) FAIL: {text}. Sửa logic để luồng chạy ĐÚNG qua các màn hình (trạng thái sau mỗi bước phản ánh nhất quán trên mọi màn liên quan) — đừng sửa assertion cho pass giả.");
        }

        return (issues, outside);
    }

    /// <summary>Số kịch bản UAT đã có mục PASS tương ứng — dùng cho dòng tóm tắt trên trang review.</summary>
    public static int CountPassing(UatScenarioSet? uat, IReadOnlyList<string> scenarioResults)
    {
        var scenarios = uat?.Scenarios ?? new List<UatScenario>();
        return scenarios.Count(s => FindMatchIndex(s.Title, scenarioResults) is var i && i >= 0
                                    && scenarioResults[i].StartsWith("PASS", StringComparison.Ordinal));
    }

    /// <summary>
    /// Các dòng của <c>pocScenarios()</c> KHÔNG ứng với kịch bản UAT nào — luồng end-to-end agent tự đặt
    /// thêm. Tách riêng để thước "luồng ngoài UAT" không đếm lại đúng những dòng thước "kịch bản nghiệm thu
    /// có mục PASS" đã đếm (trước đây hai thước đọc chung một mảng nên gần như luôn bằng nhau).
    /// </summary>
    public static List<string> OutsideUat(UatScenarioSet? uat, IReadOnlyList<string> scenarioResults)
    {
        var scenarios = uat?.Scenarios ?? new List<UatScenario>();
        var claimed = scenarios
            .Select(s => FindMatchIndex(s.Title, scenarioResults))
            .Where(i => i >= 0)
            .ToHashSet();
        return scenarioResults.Where((_, i) => !claimed.Contains(i)).ToList();
    }

    // Lượt lái chạy theo đúng thứ tự bộ UAT (và có trần số kịch bản) nên phần tử i là của kịch bản i; so
    // thêm tiêu đề để một bộ UAT đổi giữa chừng không gán nhầm kết quả của kịch bản khác.
    private static PocUatDriveResult? DriveResultFor(int index, UatScenario scenario, IReadOnlyList<PocUatDriveResult> driveResults) =>
        index < driveResults.Count && driveResults[index].Title == scenario.Title ? driveResults[index] : null;

    // Tìm dòng kết quả runtime ứng với một tiêu đề UAT. Agent được yêu cầu dùng NGUYÊN VĂN tiêu đề, nhưng
    // model hay thêm/bớt vài chữ — nên khớp theo 3 mức nới dần: bằng nhau → chứa nhau → đủ từ chung.
    // Trả chỉ số dòng (-1 nếu không khớp) để người gọi biết dòng nào đã "có chủ".
    private static int FindMatchIndex(string title, IReadOnlyList<string> scenarioResults)
    {
        if (string.IsNullOrWhiteSpace(title) || scenarioResults.Count == 0)
            return -1;

        var target = Normalize(title);
        if (target.Length == 0)
            return -1;

        var best = -1;
        var bestScore = 0d;

        for (var i = 0; i < scenarioResults.Count; i++)
        {
            var name = Normalize(ExtractName(scenarioResults[i]));
            if (name.Length == 0)
                continue;

            if (name == target)
                return i;

            var shorter = name.Length <= target.Length ? name : target;
            if (shorter.Length >= MinContainmentLength
                && (name.Contains(target, StringComparison.Ordinal) || target.Contains(name, StringComparison.Ordinal)))
                return i;

            var score = TokenOverlap(target, name);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return bestScore >= MinTokenOverlap ? best : -1;
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

    // Cùng phép chấm mà các cổng đối chiếu văn bản khác dùng (parity Brief↔Spec) — xem TextSimilarity.
    private static double TokenOverlap(string a, string b) => TextSimilarity.TokenOverlap(a, b);

    private static string Normalize(string text) => TextSimilarity.Normalize(text);

    private static string FormatTitles(IReadOnlyList<UatScenario> scenarios) =>
        string.Join("; ", scenarios.Select(s => $"'{s.Title}'"));

    private static string FormatScreen(UatScenario scenario) =>
        string.IsNullOrWhiteSpace(scenario.Screen) ? string.Empty : $" (màn hình '{scenario.Screen}')";

    private static string FormatSteps(UatScenario scenario) =>
        scenario.Steps.Count == 0 ? "(không có bước)" : string.Join(" → ", scenario.Steps);
}
