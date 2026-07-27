using System.Text.Json;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Bản chụp kết quả VÒNG KIỂM CUỐI của AuditPocContent (tĩnh + runtime + oracle + visual), lưu cạnh
/// poc-demo.html để trang POC Review cho người duyệt thấy "máy đã tự kiểm được gì" — số rule pass/fail,
/// ví dụ đã chốt tính đúng chưa, và NHỮNG ISSUE CÒN LẠI nếu agent hết 3 vòng audit mà chưa sạch. Trước
/// đây các kết quả này chỉ agent thấy trong vòng sửa rồi vứt đi, người review phải tự dò lại từ đầu.
/// </summary>
public sealed class PocVerificationSummary
{
    public DateTime CheckedAtUtc { get; set; }

    /// <summary>Số màn hình spec yêu cầu / đã phủ (từ audit tĩnh; 0/0 khi spec không parse được).</summary>
    public int SpecScreens { get; set; }
    public int CoveredScreens { get; set; }

    /// <summary>MỌI issue còn lại ở vòng audit cuối (tĩnh + runtime + oracle + visual). Rỗng = audit sạch.</summary>
    public List<string> OpenIssues { get; set; } = new();

    /// <summary>Từng dòng "PASS/FAIL BR-n — chi tiết" của window.pocSelfTest() (rỗng nếu không chạy/không định nghĩa).</summary>
    public List<string> SelfTestResults { get; set; } = new();

    /// <summary>Từng dòng "PASS/FAIL &lt;tên luồng&gt; — chi tiết" của window.pocScenarios().</summary>
    public List<string> ScenarioResults { get; set; } = new();

    /// <summary>Tổng số worked example của spec và các ví dụ POC tính SAI/thiếu (rỗng = khớp hết).</summary>
    public int WorkedExamplesTotal { get; set; }
    public List<string> WorkedExampleIssues { get; set; } = new();

    /// <summary>Tầng runtime (headless browser) có thật sự chạy không; không chạy thì vì sao.</summary>
    public bool RuntimeRan { get; set; }
    public string? RuntimeSkipReason { get; set; }

    /// <summary>Tầng Visual QA (agent UI/UX chấm ảnh) có chạy không + cảnh báo còn lại (issue visual đã gộp vào OpenIssues).</summary>
    public bool VisualRan { get; set; }
    public List<string> VisualWarnings { get; set; } = new();

    /// <summary>
    /// HỒI QUY so với vòng kiểm liền trước: những rule/kịch bản đã từng PASS mà vòng này FAIL (hoặc biến
    /// mất). Đây là lớp lỗi mà một bản chụp "chỉ vòng cuối" không thể thấy — agent sửa issue của vòng
    /// trước rồi vô tình làm gãy thứ đang chạy, và cả người review lẫn agent đều tưởng POC đang khá lên.
    /// </summary>
    public List<string> Regressions { get; set; } = new();
}

/// <summary>
/// Ghi/đọc <see cref="PocVerificationSummary"/> dưới dạng JSON trong workspace. File chính luôn phản ánh
/// vòng kiểm MỚI NHẤT; bản của các vòng trước được đẩy sang file LỊCH SỬ (capped) để so được vòng này với
/// vòng trước và bắt HỒI QUY (rule từng PASS nay FAIL) — thứ mà bản chụp "chỉ vòng cuối" không thể thấy.
/// Fail-open cả hai chiều: lưu lỗi không làm hỏng lượt audit của agent, đọc lỗi/thiếu file thì trang
/// review đơn giản là không có panel.
/// </summary>
public static class PocVerification
{
    // Cạnh poc-demo.html để đi cùng vòng đời POC (dựng lại POC ⇒ audit lại ⇒ file được ghi đè theo).
    public const string RelativePath = "04_Implementation/poc-verification.json";
    public const string HistoryRelativePath = "04_Implementation/poc-verification-history.json";

    /// <summary>Số vòng kiểm cũ được giữ lại — đủ để nhìn xu hướng qua các vòng sửa, không phình workspace.</summary>
    private const int MaxHistoryEntries = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string GetPath(string workspacePath) =>
        Path.Combine(workspacePath, "04_Implementation", "poc-verification.json");

    public static string GetHistoryPath(string workspacePath) =>
        Path.Combine(workspacePath, "04_Implementation", "poc-verification-history.json");

    public static async Task SaveAsync(string workspacePath, PocVerificationSummary summary, CancellationToken cancellationToken = default)
    {
        var path = GetPath(workspacePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        // Vòng trước (nếu có) rơi vào lịch sử TRƯỚC khi bị ghi đè — đọc lại được để so xu hướng.
        var previous = TryLoad(workspacePath);
        if (previous != null)
        {
            var history = TryLoadHistory(workspacePath);
            history.Add(previous);
            if (history.Count > MaxHistoryEntries)
                history.RemoveRange(0, history.Count - MaxHistoryEntries);
            await File.WriteAllTextAsync(
                GetHistoryPath(workspacePath), JsonSerializer.Serialize(history, SerializerOptions), cancellationToken);
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(summary, SerializerOptions), cancellationToken);
    }

    public static PocVerificationSummary? TryLoad(string workspacePath)
    {
        try
        {
            var path = GetPath(workspacePath);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<PocVerificationSummary>(File.ReadAllText(path));
        }
        catch
        {
            // File hỏng/không đọc được ⇒ coi như chưa có kết quả tự kiểm — panel tự ẩn.
            return null;
        }
    }

    public static List<PocVerificationSummary> TryLoadHistory(string workspacePath)
    {
        try
        {
            var path = GetHistoryPath(workspacePath);
            if (!File.Exists(path))
                return new List<PocVerificationSummary>();
            return JsonSerializer.Deserialize<List<PocVerificationSummary>>(File.ReadAllText(path))
                   ?? new List<PocVerificationSummary>();
        }
        catch
        {
            return new List<PocVerificationSummary>();
        }
    }

    /// <summary>
    /// Xoá kết quả tự kiểm + lịch sử. Gọi khi POC được DỰNG LẠI TỪ ĐẦU (re-seed template ở một lượt
    /// PocPreview không phải revision): kết quả của bản POC cũ không so được với bản mới, để lại chỉ sinh
    /// ra "hồi quy" giả cho một thứ không còn tồn tại.
    /// </summary>
    public static void Reset(string workspacePath)
    {
        foreach (var path in new[] { GetPath(workspacePath), GetHistoryPath(workspacePath) })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Không xoá được (file đang mở) ⇒ bỏ qua: cùng lắm là một dòng hồi quy thừa ở vòng đầu.
            }
        }
    }

    /// <summary>
    /// So kết quả vòng NÀY với vòng liền trước và trả về các dòng HỒI QUY: mục từng PASS mà nay FAIL, hoặc
    /// từng PASS mà nay biến mất khỏi bộ kiểm (agent xoá assertion thay vì sửa logic — đúng cái mẹo mà
    /// prompt POC cấm). Mục mới xuất hiện FAIL không tính là hồi quy: nó đã nằm trong ISSUES thường.
    /// </summary>
    public static List<string> DetectRegressions(
        PocVerificationSummary? previous,
        IReadOnlyList<string> currentSelfTests,
        IReadOnlyList<string> currentScenarios)
    {
        var regressions = new List<string>();
        if (previous == null)
            return regressions;

        Collect(previous.SelfTestResults, currentSelfTests, "Business rule");
        Collect(previous.ScenarioResults, currentScenarios, "Kịch bản end-to-end");
        return regressions;

        void Collect(IReadOnlyList<string> before, IReadOnlyList<string> after, string label)
        {
            var afterByName = after
                .Select(Split)
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Pass, StringComparer.OrdinalIgnoreCase);

            foreach (var (name, passedBefore) in before.Select(Split))
            {
                if (!passedBefore)
                    continue;
                if (!afterByName.TryGetValue(name, out var passesNow))
                    regressions.Add($"{label} '{name}' đã PASS ở vòng kiểm trước nhưng vòng này KHÔNG CÒN được kiểm — khôi phục assertion đó (xoá assertion không phải là cách sửa).");
                else if (!passesNow)
                    regressions.Add($"{label} '{name}' đã PASS ở vòng kiểm trước nhưng vòng này FAIL — bản sửa vừa rồi làm gãy thứ đang chạy, sửa lại cho cả hai cùng pass.");
            }
        }

        // "PASS BR-3 — chi tiết…" → (Name: "BR-3", Pass: true). Định dạng do PocRuntimeChecker dựng.
        static (string Name, bool Pass) Split(string line)
        {
            var text = (line ?? string.Empty).Trim();
            var pass = text.StartsWith("PASS", StringComparison.Ordinal);
            var rest = pass ? text[4..] : text.StartsWith("FAIL", StringComparison.Ordinal) ? text[4..] : text;
            var dash = rest.IndexOf(" — ", StringComparison.Ordinal);
            var name = (dash >= 0 ? rest[..dash] : rest).Trim();
            return (name, pass);
        }
    }
}
