using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH: một dòng «Quy tắc nghiệp vụ &amp; ràng buộc» CHỞ CON SỐ không được đứng
/// <c>[RÕ]</c> khi dự án chưa chốt được ví dụ tính thử nào (<see cref="Domain.Project.WorkedExamples"/>
/// rỗng). Chạy ở đường GHI của bản đồ bao phủ, cùng chỗ với
/// <see cref="CoverageStaleGapGuard"/> và <see cref="CoverageConfirmedTableGuard"/>.
///
/// <para>
/// <b>Vì sao con số là ca riêng.</b> <c>requirement-chat.v4.md</c> gọi công thức hiểu sai là "lỗi ĐẮT
/// nhất": tài liệu sẽ ghi đúng… điều đã hiểu sai, và mọi bước sau (kể cả POC) đều sai theo mà không cổng
/// nào bắt được — vì các cổng chỉ hỏi "có thông tin chưa", không hỏi "thông tin đó có đúng không". Cái
/// duy nhất bắt được là một ví dụ số đã được người dùng xác nhận: nó vừa là bằng chứng hiểu đúng, vừa là
/// oracle mà bản demo bị chấm theo (<c>PocWorkedExampleOracle</c>). Vì vậy prompt bắt BA "tự dựng MỘT ví
/// dụ số theo cách bạn hiểu rồi xin xác nhận" — và cho tới nay không có gì cưỡng chế điều đó.
/// </para>
///
/// <para>
/// <b>Ca thật (dự án JD Libary 5, lượt 13).</b> Người dùng nêu *"Responsibility (5 cái và có %, và có 1
/// item mặc định không được sửa là «Other task assign by manager» % từ 5-10)"*. BA ghi nhận nguyên văn rồi
/// đi tiếp; dòng «Quy tắc nghiệp vụ &amp; ràng buộc» chở đủ các con số ấy, mục "Ví dụ đã xác nhận" thì
/// trống trơn suốt buổi. Ba câu không ai trả lời: 5 là cố định hay tối thiểu, tổng % có phải bằng 100
/// không, và khoảng 5–10 là của riêng dòng mặc định hay của mọi dòng. Bản demo sẽ phải tự đoán cả ba.
/// </para>
///
/// <para>
/// <b>Một chiều, chỉ HẠ và chỉ THÊM mẩu hỏi.</b> Guard không bao giờ nâng trạng thái và không đụng tới
/// dòng đã có mẩu <c>còn thiếu:</c> riêng (mẩu của distiller cụ thể hơn mẩu dựng sẵn ở đây). Hạ nhầm —
/// một con số vô hại trong tóm tắt, ví dụ "3 vai trò" — thì cái giá là BA hỏi thêm một câu và người dùng
/// bấm một chip xác nhận; bỏ sót thì cả tài liệu lẫn POC dựng trên một công thức chưa ai kiểm. Cùng cách
/// cân giá với các chốt chặn còn lại của bản đồ.
/// </para>
///
/// <para>
/// <b>Chỉ soi nhóm quy tắc.</b> Con số nằm ở dòng «Dữ liệu / danh mục chính» (số trường, số dòng) hay
/// «Quy mô sử dụng» (số người dùng) không phải công thức và không cần oracle. Mở rộng phạm vi ra là biến
/// guard này thành một cái cổng đóng thường trực.
/// </para>
/// </summary>
public static class CoverageWorkedExampleGuard
{
    /// <summary>Nhãn nhóm bị soi — so khớp hai chiều bằng tiền tố như các guard khác.</summary>
    private const string RuleGroupLabel = "Quy tắc nghiệp vụ";

    /// <summary>
    /// Mẩu hỏi dựng sẵn. Kết bằng dấu hỏi để <see cref="RequirementReadinessGate"/> phát nguyên văn (nó chỉ
    /// nối thêm đuôi "anh/chị cho mình xin thông tin này nhé?" vào mẩu KHÔNG có dấu hỏi), và cố ý hỏi bằng
    /// ngôn ngữ người dùng: "một ví dụ cụ thể tính ra kết quả thế nào" chứ không phải "worked example".
    /// </summary>
    public const string MissingExampleGap =
        "với quy tắc có con số ở trên, anh/chị cho mình một ví dụ cụ thể tính ra kết quả thế nào?";

    /// <summary>
    /// Hạ <c>[RÕ]</c> → <c>[MỘT PHẦN]</c> và gắn mẩu hỏi cho dòng quy tắc chở con số khi
    /// <paramref name="workedExamples"/> chưa có ví dụ nào. Đã có ví dụ, hoặc dòng đã có mẩu
    /// <c>còn thiếu:</c> riêng ⇒ trả về đúng chuỗi đã nhận.
    /// </summary>
    public static string? Apply(string? coverageMap, string? workedExamples)
    {
        if (string.IsNullOrWhiteSpace(coverageMap))
            return coverageMap;

        // Có ví dụ nào đã chốt ⇒ quy tắc định lượng của dự án này đã qua một vòng kiểm chứng, guard đứng
        // ngoài. Đây là điều kiện MỘT ví dụ chứ không phải "mỗi quy tắc một ví dụ": bản đồ không mang cấu
        // trúc để nối ví dụ với quy tắc, và một cổng đòi nhiều hơn mức nó kiểm được là một cổng đóng mãi.
        if (InterviewOutlookService.ParseItems(workedExamples).Count > 0)
            return coverageMap;

        var lines = coverageMap.Replace("\r\n", "\n").Split('\n');
        var changed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var match = CoverageMapParser.LineRegex().Match(lines[i].Trim());
            if (!match.Success)
                continue;

            var label = match.Groups["label"].Value.Trim();
            if (!label.StartsWith(RuleGroupLabel, StringComparison.OrdinalIgnoreCase)
                && !RuleGroupLabel.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                continue;

            var status = CoverageMapParser.NormalizeStatus(match.Groups["status"].Value);
            if (status is not ("RÕ" or "MỘT PHẦN"))
                continue;

            var (body, evidence) = CoverageMapParser.SplitEvidence(match.Groups["summary"].Value.Trim());
            body = body.Trim();

            // Dòng đã có mẩu hỏi riêng ⇒ để nguyên: mẩu của distiller bám vào đúng quy tắc còn hụt, cụ thể
            // hơn mẩu dựng sẵn ở đây, và chồng hai mẩu lên nhau thì cổng phát ra một câu hỏi kép.
            if (body.Contains("còn thiếu:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!CarriesNumber(body))
                continue;

            if (body.Length > 0 && !body.EndsWith('.') && !body.EndsWith(';'))
                body += ".";

            var rebuilt = (body + " còn thiếu: " + MissingExampleGap).Trim();
            if (evidence.Length > 0)
                rebuilt += " {nguồn: " + evidence + "}";

            lines[i] = "- " + (match.Groups["core"].Success ? "★ " : string.Empty)
                + label + ": [MỘT PHẦN] " + rebuilt;
            changed = true;
        }

        return changed ? string.Join("\n", lines) : coverageMap;
    }

    /// <summary>
    /// Tóm tắt dòng có chở con số/tỷ lệ không. Đọc CHỮ SỐ chứ không đọc từ chỉ số lượng ("vài", "một
    /// nửa"): một quy tắc đáng có ví dụ tính thử thì gần như luôn viết ra con số, còn bắt theo từ thì
    /// guard hạ nhầm cả các dòng chỉ mô tả.
    /// </summary>
    private static bool CarriesNumber(string body)
        => body.Contains('%', StringComparison.Ordinal) || DigitRegex.IsMatch(body);

    private static readonly Regex DigitRegex = new(@"\d", RegexOptions.Compiled);
}
