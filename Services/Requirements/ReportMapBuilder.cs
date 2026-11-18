using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using static ICOGenerator.Services.Requirements.RequirementText;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng báo cáo / thống kê": mỗi báo cáo một dòng, kèm câu hỏi nó trả lời, bản ghi nó
/// lấy số, và các chiều gộp/lọc. Xem <see cref="ReportMapRow"/> cho lý do đầy đủ.
///
/// <para>
/// <b>Bảng này do BA RÁP, người dùng không rà nó.</b> <see cref="ReportMapDraftService"/> ráp nó từ các báo
/// cáo người dùng đã chọn / đã kể ngay khi nhóm «Báo cáo / thống kê» lên <c>[RÕ]</c>, rồi gieo chúng vào
/// bảng màn hình ở trạng thái chưa chốt (<see cref="ReportScreenAdditions"/>) để lượt soạn phạm vi
/// (<see cref="ScreenScopeDraftService"/>) đặt chúng vào phạm vi màn hình.
/// </para>
///
/// <para>
/// <b>Các báo cáo GOM về MỘT màn hình tổng quan, không phải mỗi báo cáo một màn hình.</b> Người dùng mở
/// báo cáo để trả lời một câu "tình hình hôm nay thế nào" — rải ba báo cáo ra ba mục menu là bắt họ mở ba
/// màn hình cho một câu hỏi, và hai trong ba thường chỉ là danh sách của một màn hình làm việc lọc theo
/// trạng thái. Nên mặc định mỗi báo cáo là một CHỨC NĂNG của màn tổng quan
/// (<see cref="ReportMapRow.Screen"/>); nó chỉ đứng thành màn hình riêng khi có lý do thật
/// (<see cref="ReportMapRow.OwnScreen"/>: người xem / phạm vi dữ liệu khác, hoặc bảng chi tiết theo kỳ để
/// xuất file), và báo cáo dạng danh sách lọc thì chỉ là một ô đếm dẫn sang màn hình làm việc
/// (<see cref="ReportMapRow.FilteredList"/>).
/// </para>
///
/// <para>
/// <b>Vì sao nó phải có mặt TRƯỚC bảng màn hình, và không cần cột "ai xem".</b> Màn tổng quan và mỗi báo
/// cáo có màn riêng đều là dòng bình thường của bảng màn hình rồi thành DÒNG của bảng phân quyền — nơi "ai
/// xem" đã có cột riêng kèm PHẠM VI DỮ LIỆU. Ráp sau bảng phân quyền thì chúng không có dòng quyền nào và
/// không có mục nào ở <c>## 6. Screens To Generate</c>: mặc nhiên "không ai được xem" thứ người dùng vừa
/// đặt hàng. Quyền chốt theo MÀN HÌNH cũng chính là lý do báo cáo có người xem khác phải tách màn riêng.
/// </para>
///
/// <para>
/// Ô "lấy số từ" là chữ TỰ DO model ráp từ hội thoại. Không có danh sách đối tượng nào để đối chiếu — mô
/// hình dữ liệu được thiết kế ở bước sinh AI Design Spec, và bước đó đọc ô này như một gợi ý để nối báo cáo
/// về đúng entity.
/// </para>
///
/// <para>
/// <b>Mọi dòng ra khỏi <see cref="Build"/> đều ĐƯỢC GIỮ</b>, bất kể model trả gì. Cờ <c>included</c> là chỗ
/// NGƯỜI DÙNG nói "báo cáo này không cần" (qua bảng màn hình), không phải chỗ model tự phủ nhận đề xuất của
/// chính nó — mà structured output thì luôn phải điền đủ trường, nên một model điền <c>false</c> cho có sẽ
/// âm thầm loại sạch bảng.
/// </para>
/// </summary>
public static class ReportMapBuilder
{
    /// <summary>
    /// Trần số báo cáo. Nhiều hơn thì bảng không rà nổi trong một lượt, và phần thừa luôn là báo cáo bịa —
    /// "thống kê theo tháng / quý / năm" tách thành ba dòng là ba màn hình cho cùng một câu hỏi.
    /// </summary>
    public const int MaxRows = 12;

    private const int MaxTextChars = 200;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Các từ khiến một cái tên tự đọc được như MỘT MÀN HÌNH — tra ở BẤT KỲ đâu trong tên, vì hình dạng
    /// tên màn hình là hậu tố ("Headcount Report"). Tên không chứa từ nào ở đây được
    /// <see cref="OwnScreenName"/> gắn thêm hậu tố — xem hàm đó.
    /// </summary>
    private static readonly string[] ScreenLikeWords =
        { "report", "dashboard", "statistics", "analytics", "overview" };

    /// <summary>Các từ khiến một cái tên tự đọc được như MÀN TỔNG QUAN — xem <see cref="DashboardName"/>.</summary>
    private static readonly string[] DashboardWords = { "dashboard", "overview" };

    /// <summary>Tên màn tổng quan khi model không đặt được tên nào dùng được.</summary>
    public const string DefaultDashboard = "Overview Dashboard";

    /// <summary>Câu "việc của màn" gieo sẵn cho màn tổng quan — bản tạm, lượt BA bày bảng màn hình viết lại.</summary>
    public const string DashboardPurpose = "Xem tổng quan các số liệu chính trên cùng một màn hình";

    private const int MaxDashboardNameChars = 60;

    /// <summary>
    /// Bảng cuối cùng từ đề xuất của model: bỏ dòng không tên/trùng tên, cắt ở trần, rồi XẾP CHỖ từng báo
    /// cáo (<see cref="ReportMapRow.Screen"/>): màn riêng của nó, hoặc màn tổng quan
    /// <paramref name="dashboard"/>. Trả rỗng khi không đề xuất nào dùng được — caller fail-open, không ghi
    /// gì và lượt kế ráp lại.
    ///
    /// <para>
    /// Chỗ đứng do CODE gán, không lấy chữ model điền ở <c>screen</c>: nó là khoá nối báo cáo với màn hình
    /// của nó trong bảng màn hình. Danh sách lọc
    /// (<see cref="ReportMapRow.FilteredList"/>) không bao giờ có màn riêng — dựng nó thành màn hình là
    /// dựng lại đúng cái danh sách mà màn hình làm việc đã có.
    /// </para>
    /// </summary>
    public static List<ReportMapRow> Build(IEnumerable<ReportMapRow>? proposed, string? dashboard = null)
    {
        var dashboardName = DashboardName(dashboard);
        var result = new List<ReportMapRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in proposed ?? Enumerable.Empty<ReportMapRow>())
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Report))
                continue;

            var name = Clip(row.Report.Trim(), MaxTextChars);
            if (!seen.Add(Normalize(name)))
                continue;

            var ownScreen = row.OwnScreen && !row.FilteredList;
            result.Add(new ReportMapRow
            {
                Report = name,
                Question = Clip((row.Question ?? string.Empty).Trim(), MaxTextChars),
                Source = Clip((row.Source ?? string.Empty).Trim(), MaxTextChars),
                Breakdown = Clip((row.Breakdown ?? string.Empty).Trim(), MaxTextChars),
                OwnScreen = ownScreen,
                FilteredList = row.FilteredList,
                Screen = ownScreen ? OwnScreenName(name) : dashboardName,
                Included = true
            });

            if (result.Count >= MaxRows)
                break;
        }

        return result;
    }

    /// <summary>Đọc JSON bảng báo cáo đã lưu (cột DB hoặc payload client). null/rỗng/hỏng ⇒ mảng rỗng.</summary>
    public static List<ReportMapRow> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<ReportMapRow>();

        try
        {
            var rows = JsonSerializer.Deserialize<List<ReportMapRow>>(json, ReadOptions) ?? new List<ReportMapRow>();
            return rows.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Report)).ToList();
        }
        catch
        {
            return new List<ReportMapRow>();
        }
    }

    /// <summary>Dự án này đã có bảng báo cáo chưa.</summary>
    public static bool IsConfirmed(string? json) => Parse(json).Count > 0;

    /// <summary>
    /// Tên màn tổng quan: chữ model đặt (<c>ReportMapDraft.Dashboard</c>) đã chuẩn hoá. Rỗng ⇒
    /// <see cref="DefaultDashboard"/>; tên không tự nói ra nó là màn tổng quan thì được gắn hậu tố
    /// <c>Dashboard</c> — cùng lý do với hậu tố <c>Report</c> ở <see cref="OwnScreenName"/>.
    /// </summary>
    public static string DashboardName(string? proposed)
    {
        var name = Clip(string.Join(' ', (proposed ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), MaxDashboardNameChars).Trim();
        if (name.Length == 0)
            return DefaultDashboard;

        return DashboardWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? name : $"{name} Dashboard";
    }

    /// <summary>
    /// Các MÀN HÌNH mà bảng báo cáo đẻ ra — màn tổng quan (một lần, dù gom bao nhiêu báo cáo) và màn riêng
    /// của từng báo cáo có <see cref="ReportMapRow.OwnScreen"/>. Chỉ tính báo cáo còn tích.
    /// </summary>
    public static List<string> ReportScreens(IEnumerable<ReportMapRow>? rows)
        => ReportScreenAdditions(rows).Select(a => a.Screen).ToList();

    /// <summary>Bản đọc từ JSON đã lưu của <see cref="ReportScreens(IEnumerable{ReportMapRow})"/>.</summary>
    public static List<string> ReportScreens(string? json) => ReportScreens(Parse(json));

    /// <summary>
    /// Tên màn hình RIÊNG của một báo cáo. Nó phải tự đọc được như một màn hình, vì cột "Màn hình" của bảng
    /// màn hình chỉ được chứa màn hình — và phải NGẮN, bằng TIẾNG ANH, vì nó thành nhãn mục menu của bản
    /// demo. Tên ở bảng báo cáo thường đã tự nói ra điều đó ("Headcount Report") nên giữ nguyên; chỉ tên
    /// trần ("Remaining Leave") mới được gắn hậu tố, chứ gắn cho tất cả thì sinh ra "Headcount Report Report".
    /// </summary>
    private static string OwnScreenName(string report)
    {
        var name = report.Trim();
        return ReadsAsScreen(name) ? name : $"{name} Report";
    }

    /// <summary>
    /// Màn hình mà một báo cáo ĐỨNG TRÊN. Dòng không có <see cref="ReportMapRow.Screen"/> là dòng ráp từ
    /// hồi mỗi báo cáo còn là một màn hình: nó đứng trên màn riêng mang tên nó.
    /// </summary>
    private static string HostScreen(ReportMapRow row)
        => string.IsNullOrWhiteSpace(row.Screen) ? OwnScreenName(row.Report) : row.Screen.Trim();

    /// <summary>Báo cáo này là một CHỨC NĂNG của màn tổng quan (chứ không phải một màn hình riêng).</summary>
    private static bool OnDashboard(ReportMapRow row)
        => !row.OwnScreen && !string.IsNullOrWhiteSpace(row.Screen);

    /// <summary>
    /// Tên CHỨC NĂNG mà một báo cáo gieo lên màn tổng quan: <c>"&lt;tên&gt; — &lt;để làm gì&gt;"</c>. Tên báo
    /// cáo đứng ĐẦU để chức năng tự nói nó là báo cáo nào; vế sau là lời người dùng — câu hỏi mà báo cáo
    /// trả lời, thứ đi tiếp vào spec và bản demo.
    /// </summary>
    private static string DashboardFunction(ReportMapRow row)
    {
        var name = row.Report.Trim();
        var question = (row.Question ?? string.Empty).Trim();
        if (question.Length > 0)
            name += " — " + question;
        if (row.FilteredList)
            name += " (ô số, bấm vào mở danh sách đã lọc)";
        return Clip(name, MaxTextChars);
    }

    /// <summary>
    /// Các mục GIEO vào bảng màn hình (<see cref="ScreenScopeMapBuilder.Merge"/>). Báo cáo có màn riêng ⇒
    /// một màn hình, ô "việc của màn" điền sẵn bằng câu hỏi báo cáo trả lời. Các báo cáo còn lại ⇒ MỘT màn
    /// tổng quan, mỗi báo cáo một chức năng (mỗi chức năng một dòng của bảng phân quyền).
    /// </summary>
    public static List<ScopeAddition> ReportScreenAdditions(IEnumerable<ReportMapRow>? rows)
    {
        var result = new List<ScopeAddition>();
        var byScreen = new Dictionary<string, ScopeAddition>(StringComparer.Ordinal);

        foreach (var row in (rows ?? Enumerable.Empty<ReportMapRow>())
                     .Where(r => r != null && r.Included && !string.IsNullOrWhiteSpace(r.Report)))
        {
            var screen = HostScreen(row);
            var onDashboard = OnDashboard(row);

            if (!byScreen.TryGetValue(Normalize(screen), out var addition))
            {
                addition = new ScopeAddition
                {
                    Screen = screen,
                    Purpose = onDashboard ? DashboardPurpose : (row.Question ?? string.Empty).Trim()
                };
                byScreen[Normalize(screen)] = addition;
                result.Add(addition);
            }

            if (onDashboard)
                addition.Functions.Add(DashboardFunction(row));
        }

        return result;
    }

    /// <summary>Tên đã tự đọc được như một màn hình — xem <see cref="ScreenLikeWords"/>.</summary>
    private static bool ReadsAsScreen(string name) =>
        ScreenLikeWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Khối ngữ cảnh gắn vào MỌI lượt chat sau khi bảng đã ráp, vào lượt distill bản đồ bao phủ, và vào
    /// prompt sinh AI Design Spec. Không có khối này thì BA vẫn hỏi lại đúng các báo cáo người dùng vừa
    /// chọn, và spec phải đoán lại "lấy số từ đâu / gộp theo gì". Trả null khi chưa có bảng.
    ///
    /// <para>
    /// <b>Mọi báo cáo đều đã bị bỏ vẫn ra khối</b>, khác <see cref="FlowMapBuilder.RenderConfirmedBlock"/>:
    /// báo cáo bị loại hết (dữ liệu của dự án cũ, thời người dùng còn bỏ tích báo cáo ở bảng màn hình) là
    /// một QUYẾT ĐỊNH, và là quyết định dễ
    /// bị hỏi lại nhất nếu không nói ra — trả null ở ca đó là để BA đề xuất lại đúng danh sách vừa bị tắt.
    /// </para>
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        var all = Parse(json);
        if (all.Count == 0)
            return null;

        var kept = all.Where(r => r.Included).ToList();
        if (kept.Count == 0)
            return "\n--- Bảng báo cáo / thống kê của ứng dụng (đừng hỏi lại) ---\n"
                + "Người dùng đã bỏ HẾT các báo cáo ở bảng màn hình: ứng dụng KHÔNG cần báo cáo/thống kê nào. "
                + "Đây là quyết định của họ — KHÔNG đề xuất lại, KHÔNG hỏi lại nhóm «Báo cáo / thống kê».";

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng báo cáo / thống kê của ứng dụng (BA ráp từ các báo cáo NGƯỜI DÙNG ĐÃ CHỌN — đừng hỏi lại) ---");
        sb.AppendLine("Mỗi dòng: tên báo cáo · trả lời câu hỏi gì · lấy số từ bản ghi nào · gộp/lọc theo gì.");

        // Báo cáo GOM trên màn tổng quan đứng trước, nhóm theo màn: tầng sau (bảng màn hình, spec) phải đọc ra
        // được rằng chúng là các PHẦN của một màn hình, không phải mỗi dòng một màn hình.
        foreach (var group in kept.Where(OnDashboard).GroupBy(r => Normalize(HostScreen(r))))
        {
            sb.AppendLine($"Màn hình tổng quan «{HostScreen(group.First())}» GOM các báo cáo dưới đây — mỗi báo cáo là MỘT PHẦN của màn hình này, KHÔNG phải một màn hình riêng:");
            foreach (var row in group)
                sb.AppendLine("- " + RenderRow(row));
        }

        var own = kept.Where(r => !OnDashboard(r)).ToList();
        if (own.Count > 0)
        {
            sb.AppendLine("Mỗi báo cáo dưới đây là MỘT MÀN HÌNH riêng:");
            foreach (var row in own)
                sb.AppendLine("- " + RenderRow(row) + " · màn hình: " + HostScreen(row));
        }

        // Báo cáo bị loại phải được NÓI RA ở đây: thiếu nó thì lượt sau BA thấy một nhu cầu người dùng từng
        // nhắc mà bảng không có, và nó đề xuất lại đúng thứ vừa bị tắt.
        var dropped = all.Where(r => !r.Included).Select(r => r.Report.Trim()).ToList();
        if (dropped.Count > 0)
            sb.AppendLine("KHÔNG cần các báo cáo sau (người dùng đã bỏ chúng ở bảng màn hình): " + string.Join("; ", dropped));

        return sb.ToString().TrimEnd();
    }

    // ==== chuẩn hoá từng phần ====

    private static string RenderRow(ReportMapRow row)
    {
        var parts = new List<string> { row.Report.Trim() };
        if (!string.IsNullOrWhiteSpace(row.Question))
        {
            // Ô "để trả lời câu hỏi gì" gần như luôn được gõ/điền sẵn dưới dạng một mệnh đề mục đích đã có
            // sẵn chữ "để" ("để biết ai chưa đi học"), vì đó là cách người ta nói. Cứ chắp thêm tiền tố là
            // ra "để để biết…" ở khối ngữ cảnh.
            var question = row.Question.Trim();
            parts.Add(question.StartsWith("để", StringComparison.OrdinalIgnoreCase) ? question : "để " + question);
        }
        if (!string.IsNullOrWhiteSpace(row.Source))
            parts.Add("lấy số từ: " + row.Source.Trim());
        if (!string.IsNullOrWhiteSpace(row.Breakdown))
            parts.Add("gộp/lọc theo: " + row.Breakdown.Trim());
        if (row.FilteredList)
            parts.Add("chỉ là Ô ĐẾM: bấm vào thì mở màn hình làm việc đang giữ các bản ghi đó, đã lọc sẵn — KHÔNG dựng lại danh sách ở đây");
        return string.Join(" · ", parts);
    }
}
