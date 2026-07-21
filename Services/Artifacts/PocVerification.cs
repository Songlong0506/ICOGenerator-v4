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
}

/// <summary>
/// Ghi/đọc <see cref="PocVerificationSummary"/> dưới dạng JSON trong workspace. Mỗi lần AuditPocContent
/// chạy là một lần GHI ĐÈ — file luôn phản ánh vòng kiểm MỚI NHẤT (kể cả các vòng audit của lượt
/// "Yêu cầu chỉnh sửa" sau này). Fail-open cả hai chiều: lưu lỗi không làm hỏng lượt audit của agent,
/// đọc lỗi/thiếu file thì trang review đơn giản là không có panel.
/// </summary>
public static class PocVerification
{
    // Cạnh poc-demo.html để đi cùng vòng đời POC (dựng lại POC ⇒ audit lại ⇒ file được ghi đè theo).
    public const string RelativePath = "04_Implementation/poc-verification.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string GetPath(string workspacePath) =>
        Path.Combine(workspacePath, "04_Implementation", "poc-verification.json");

    public static async Task SaveAsync(string workspacePath, PocVerificationSummary summary, CancellationToken cancellationToken = default)
    {
        var path = GetPath(workspacePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
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
}
