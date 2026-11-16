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
/// cáo người dùng đã chọn / đã kể ngay khi nhóm «Báo cáo / thống kê» lên <c>[RÕ]</c>, rồi gieo mỗi dòng
/// thành một MÀN HÌNH chờ duyệt (<see cref="ReportScreenAdditions"/> → bảng màn hình). Chỗ người dùng rà
/// báo cáo là bảng màn hình: bỏ tích dòng màn hình ở đó là bỏ báo cáo (<see cref="DropUnscreened"/>).
/// </para>
///
/// <para>
/// <b>Vì sao nó phải có mặt TRƯỚC bảng màn hình, và không cần cột "ai xem".</b> Mỗi báo cáo là một dòng
/// bình thường của bảng màn hình rồi thành DÒNG của bảng phân quyền — nơi "ai xem" đã có cột riêng kèm
/// PHẠM VI DỮ LIỆU. Ráp sau bảng phân quyền thì báo cáo không có dòng quyền nào và không có mục nào ở
/// <c>## 6. Screens To Generate</c>: mặc nhiên "không ai được xem" một màn hình người dùng vừa đặt hàng.
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
    /// <see cref="ReportScreens(IEnumerable{ReportMapRow})"/> gắn thêm hậu tố — xem hàm đó.
    /// </summary>
    private static readonly string[] ScreenLikeWords =
        { "report", "dashboard", "statistics", "analytics", "overview" };

    /// <summary>
    /// Bảng cuối cùng từ đề xuất của model: bỏ dòng không tên/trùng tên, cắt ở trần. Trả rỗng khi không đề
    /// xuất nào dùng được — caller fail-open, không ghi gì và lượt kế ráp lại.
    /// </summary>
    public static List<ReportMapRow> Build(IEnumerable<ReportMapRow>? proposed)
    {
        var result = new List<ReportMapRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in proposed ?? Enumerable.Empty<ReportMapRow>())
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Report))
                continue;

            var name = Clip(row.Report.Trim(), MaxTextChars);
            if (!seen.Add(Normalize(name)))
                continue;

            result.Add(new ReportMapRow
            {
                Report = name,
                Question = Clip((row.Question ?? string.Empty).Trim(), MaxTextChars),
                Source = Clip((row.Source ?? string.Empty).Trim(), MaxTextChars),
                Breakdown = Clip((row.Breakdown ?? string.Empty).Trim(), MaxTextChars),
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
    /// Các MÀN HÌNH mà bảng báo cáo đẻ ra — mỗi báo cáo còn tích là một chỗ người dùng mở ra và nhìn thấy,
    /// tức một mục phạm vi thật. Đây là đường tiêu thụ chính của bảng: xem ghi chú đầu class.
    ///
    /// <para>
    /// Tên gieo ra phải tự đọc được như một màn hình, vì cột "Màn hình" của bảng màn hình chỉ được chứa màn
    /// hình — và phải NGẮN, bằng TIẾNG ANH, vì nó thành nhãn mục menu của bản demo. Tên ở bảng báo cáo
    /// thường đã tự nói ra điều đó ("Headcount Report") nên giữ nguyên; chỉ tên trần ("Remaining Leave")
    /// mới được gắn hậu tố, chứ gắn cho tất cả thì sinh ra "Headcount Report Report".
    /// </para>
    /// </summary>
    public static List<string> ReportScreens(IEnumerable<ReportMapRow>? rows)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in (rows ?? Enumerable.Empty<ReportMapRow>())
                     .Where(r => r != null && r.Included && !string.IsNullOrWhiteSpace(r.Report)))
        {
            var screen = ScreenOf(row);
            if (seen.Add(Normalize(screen)))
                result.Add(screen);
        }

        return result;
    }

    /// <summary>Bản đọc từ JSON đã lưu của <see cref="ReportScreens(IEnumerable{ReportMapRow})"/>.</summary>
    public static List<string> ReportScreens(string? json) => ReportScreens(Parse(json));

    /// <summary>Tên màn hình mà MỘT báo cáo gieo ra — cùng luật hậu tố với <see cref="ReportScreens(IEnumerable{ReportMapRow})"/>.</summary>
    private static string ScreenOf(ReportMapRow row)
    {
        var name = row.Report.Trim();
        return ReadsAsScreen(name) ? name : $"{name} Report";
    }

    /// <summary>
    /// Các mục GIEO vào bảng màn hình (<see cref="ScreenScopeMapBuilder.Merge"/>): mỗi báo cáo một màn hình,
    /// ô "việc của màn" điền sẵn bằng câu hỏi báo cáo trả lời — thứ người dùng nhận ra ngay khi rà bảng màn
    /// hình, thay cho một dòng chỉ có tên.
    /// </summary>
    public static List<ScopeAddition> ReportScreenAdditions(IEnumerable<ReportMapRow>? rows)
    {
        var result = new List<ScopeAddition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in (rows ?? Enumerable.Empty<ReportMapRow>())
                     .Where(r => r != null && r.Included && !string.IsNullOrWhiteSpace(r.Report)))
        {
            var screen = ScreenOf(row);
            if (seen.Add(Normalize(screen)))
                result.Add(new ScopeAddition { Screen = screen, Purpose = (row.Question ?? string.Empty).Trim() });
        }

        return result;
    }

    /// <summary>
    /// Đồng bộ bảng báo cáo theo bảng màn hình NGƯỜI DÙNG VỪA CHỐT: báo cáo nào có màn hình bị bỏ tích thì
    /// bị đánh dấu loại. Trả JSON mới, hoặc null khi không có gì đổi.
    ///
    /// <para>
    /// Đây là đường DUY NHẤT người dùng bỏ được một báo cáo, vì bảng báo cáo không bày ra cho họ rà. Thiếu
    /// nó thì khối ngữ cảnh và prompt sinh spec vẫn kể báo cáo ấy như một thứ ứng dụng phải có, trong khi
    /// màn hình của nó đã bị loại khỏi phạm vi — hai nguồn chọi nhau và tầng sau không biết tin bên nào.
    /// Chỉ một chiều (bỏ), không tích lại: dòng màn hình đã bỏ tích là BIA, không lượt nào mở lại nó.
    /// </para>
    /// </summary>
    public static string? DropUnscreened(string? json, IEnumerable<ScreenScopeRow> screens)
    {
        var rows = Parse(json);
        var dropped = new HashSet<string>(
            screens.Where(s => !s.Included).Select(s => Normalize(s.Screen)), StringComparer.Ordinal);

        var changed = false;
        foreach (var row in rows.Where(r => r.Included && dropped.Contains(Normalize(ScreenOf(r)))))
        {
            row.Included = false;
            changed = true;
        }

        return changed ? JsonSerializer.Serialize(rows) : null;
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
    /// người dùng bỏ tích hết các màn hình báo cáo ở bảng màn hình là một QUYẾT ĐỊNH, và là quyết định dễ
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
                + "Người dùng đã bỏ HẾT các màn hình báo cáo ở bảng màn hình: ứng dụng KHÔNG cần báo cáo/thống kê nào. "
                + "Đây là quyết định của họ — KHÔNG đề xuất lại, KHÔNG hỏi lại nhóm «Báo cáo / thống kê».";

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng báo cáo / thống kê của ứng dụng (BA ráp từ các báo cáo NGƯỜI DÙNG ĐÃ CHỌN — đừng hỏi lại) ---");
        sb.AppendLine("Mỗi dòng: tên báo cáo · trả lời câu hỏi gì · lấy số từ bản ghi nào · gộp/lọc theo gì.");

        foreach (var row in kept)
            sb.AppendLine("- " + RenderRow(row));

        // Báo cáo bị loại phải được NÓI RA ở đây: thiếu nó thì lượt sau BA thấy một nhu cầu người dùng từng
        // nhắc mà bảng không có, và nó đề xuất lại đúng thứ vừa bị tắt.
        var dropped = all.Where(r => !r.Included).Select(r => r.Report.Trim()).ToList();
        if (dropped.Count > 0)
            sb.AppendLine("KHÔNG cần các báo cáo sau (người dùng đã bỏ màn hình của chúng): " + string.Join("; ", dropped));

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
        return string.Join(" · ", parts);
    }
}
