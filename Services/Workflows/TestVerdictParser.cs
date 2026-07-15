using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Workflows;

/// <summary>Kết luận kiểm thử do Tester chốt ở cuối báo cáo.</summary>
public enum TestVerdict
{
    /// <summary>Không tìm thấy dòng VERDICT hợp lệ — xử lý như PASS để giữ hành vi cũ (không tự lặp sửa).</summary>
    Unknown,
    Pass,
    Fail
}

/// <summary>
/// Đọc dòng máy-đọc-được <c>VERDICT: PASS|FAIL</c> mà Tester bắt buộc ghi ở cuối tóm tắt
/// (xem <c>Prompts/Tester/testing.v1.md</c>). Đây là tín hiệu để worker quyết định có
/// kích hoạt vòng tự sửa lỗi hay không — tách riêng & thuần để dễ kiểm thử.
/// </summary>
public static class TestVerdictParser
{
    // Khoan dung với định dạng model hay sinh: '**bold**', ':' hoặc '=', hoa/thường, khoảng trắng thừa.
    private static readonly Regex Marker = new(
        @"VERDICT\s*[:=]\s*\*{0,2}\s*(PASS|FAIL)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static TestVerdict Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return TestVerdict.Unknown;

        var matches = Marker.Matches(output);
        if (matches.Count == 0)
            return TestVerdict.Unknown;

        // Lấy lần xuất hiện CUỐI: tóm tắt kết luận thường nằm ở cuối output, sau khi báo cáo
        // có thể đã nhắc PASS/FAIL của từng suite lẻ phía trên.
        var verdict = matches[^1].Groups[1].Value;
        return verdict.Equals("FAIL", StringComparison.OrdinalIgnoreCase)
            ? TestVerdict.Fail
            : TestVerdict.Pass;
    }
}
