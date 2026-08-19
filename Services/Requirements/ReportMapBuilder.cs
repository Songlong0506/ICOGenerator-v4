using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng báo cáo / thống kê" — bảng THỨ BA trong sáu bảng chốt của buổi phỏng vấn: mỗi
/// báo cáo một dòng, kèm câu hỏi nó trả lời, đối tượng nó lấy số, và các chiều gộp/lọc. Xem
/// <see cref="ReportMapRow"/> cho lý do đầy đủ.
///
/// <para>
/// <b>Bảng này ĐỨNG TRƯỚC bảng màn hình rồi bảng phân quyền, và đó là toàn bộ lý do nó không cần cột "ai
/// xem".</b> Mỗi dòng được giữ sẽ thành một mục phạm vi (<see cref="ReportScreens"/> →
/// <c>Project.PlannedScope</c>) trước khi bảng màn hình bày ra lần đầu, nên nó là một dòng bình thường của
/// bảng màn hình rồi thành DÒNG của bảng phân quyền — nơi "ai xem" đã có cột riêng kèm PHẠM VI DỮ LIỆU.
/// Hỏi sau bảng phân quyền thì mọi báo cáo vừa chốt không có dòng quyền nào và không có mục nào ở
/// <c>## 6. Screens To Generate</c>: mặc nhiên "không ai được xem" một màn hình người dùng vừa đặt hàng —
/// đúng loại quyết định câm mà cả bộ bảng sinh ra để chặn (cùng hình dạng với màn hình danh mục của
/// <see cref="EntityMapBuilder.ManagedListScreens(IEnumerable{EntityMapRow})"/>).
/// </para>
///
/// <para>
/// Hai chốt chặn tất định:
/// </para>
/// <list type="bullet">
///   <item><b>Nguồn số liệu bịa bị XÓA, không bị lấy nguyên.</b> Ô "lấy số từ" chỉ giữ khi nó khớp một đối
///   tượng của bảng đối tượng ĐÃ CHỐT — mối nối giữa hai bảng là thứ giữ cho bước sinh spec không tự nghĩ
///   ra một nguồn dữ liệu. Không khớp thì ô về RỖNG chứ dòng không bị loại: một cái tên nguồn sai không
///   làm cả báo cáo trở nên sai, và người dùng còn thấy dòng để tự sửa.</item>
///   <item><b>Mọi dòng ra khỏi <see cref="Build"/> đều ĐƯỢC GIỮ</b>, bất kể model trả gì. Cờ
///   <c>included</c> là chỗ NGƯỜI DÙNG nói "báo cáo này không cần", không phải chỗ model tự phủ nhận đề
///   xuất của chính nó — mà structured output thì luôn phải điền đủ trường, nên một model điền
///   <c>false</c> cho có sẽ âm thầm loại sạch bảng.</item>
/// </list>
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
    /// Các tiền tố khiến một cái tên tự đọc được như MỘT MÀN HÌNH. Tên không có tiền tố nào ở đây được
    /// <see cref="ReportScreens"/> gắn thêm chữ dẫn — xem hàm đó.
    /// </summary>
    private static readonly string[] ScreenLikePrefixes =
        { "báo cáo", "thống kê", "dashboard", "màn hình", "trang", "bảng điều khiển" };

    /// <summary>
    /// Bảng cuối cùng cho lượt BA BÀY BẢNG: bỏ dòng không tên/trùng tên, cắt ở trần, và xoá ô nguồn không
    /// khớp đối tượng nào trong <paramref name="entityNames"/>. Trả rỗng khi không đề xuất nào dùng được —
    /// caller fail-open, lượt chạy như một lượt chat thường và cổng mở lại ở lượt sau.
    /// </summary>
    public static List<ReportMapRow> Build(IEnumerable<ReportMapRow>? proposed, IReadOnlyList<string>? entityNames)
        => BuildCore(proposed, entityNames, respectIncluded: false);

    /// <summary>
    /// Bản chuẩn hoá cho dữ liệu ĐẾN TỪ TRÌNH DUYỆT. Cùng luật với <see cref="Build"/> — server không tin
    /// bảng client gửi kể cả khi chính nó vừa render ra — khác đúng một điểm: đường này TÔN TRỌNG cờ
    /// <c>included</c>, vì ở đây nó là lựa chọn của người dùng chứ không phải của model.
    ///
    /// <para>
    /// Dòng người dùng BỎ TÍCH vẫn được giữ lại ở đây (không lọc): tin nhắn gửi vào hội thoại phải kể được
    /// rằng báo cáo đó đã bị loại — không có bằng chứng ấy thì họ không biết mình vừa loại đúng thứ định
    /// loại. Các đường tiêu thụ phía sau (khối ngữ cảnh, phạm vi màn hình, spec) mới là chỗ lọc.
    /// </para>
    /// </summary>
    public static List<ReportMapRow> Sanitize(IEnumerable<ReportMapRow>? submitted, IReadOnlyList<string>? entityNames)
        => BuildCore(submitted, entityNames, respectIncluded: true);

    private static List<ReportMapRow> BuildCore(
        IEnumerable<ReportMapRow>? rows, IReadOnlyList<string>? entityNames, bool respectIncluded)
    {
        var result = new List<ReportMapRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows ?? Enumerable.Empty<ReportMapRow>())
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
                Source = MatchEntity(row.Source, entityNames),
                Breakdown = Clip((row.Breakdown ?? string.Empty).Trim(), MaxTextChars),
                Included = !respectIncluded || row.Included,
                // Cờ này chỉ được đọc ở đường GỬI: ở lượt bày bảng mọi dòng đều do model soạn, nên đọc nó ở
                // đó là dựng cho model một cửa sau tự khai là người dùng. Cùng luật với ScreenScopeRow.
                AddedByUser = respectIncluded && row.AddedByUser
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

    /// <summary>Dự án này đã chốt bảng báo cáo chưa.</summary>
    public static bool IsConfirmed(string? json) => Parse(json).Count > 0;

    /// <summary>
    /// Các MÀN HÌNH mà bảng báo cáo đẻ ra — mỗi báo cáo còn tích là một chỗ người dùng mở ra và nhìn thấy,
    /// tức một mục phạm vi thật. Đây là đường tiêu thụ chính của bảng: xem ghi chú đầu class.
    ///
    /// <para>
    /// Tên gieo ra phải tự đọc được như một màn hình, vì cột "Màn hình" của bảng màn hình chỉ được chứa màn
    /// hình. Tên người dùng gõ thường đã tự nói ra điều đó ("Báo cáo tổng hợp ngày phép còn lại") nên giữ
    /// nguyên; tên trần ("Ngày phép còn lại") mới được gắn chữ dẫn, chứ gắn cho tất cả thì sinh ra những
    /// mục đọc như "Màn hình báo cáo Báo cáo tổng hợp…".
    /// </para>
    /// </summary>
    public static List<string> ReportScreens(IEnumerable<ReportMapRow>? rows)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in (rows ?? Enumerable.Empty<ReportMapRow>())
                     .Where(r => r != null && r.Included && !string.IsNullOrWhiteSpace(r.Report)))
        {
            var name = row.Report.Trim();
            var screen = ScreenLikePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                ? name
                : $"Báo cáo {name}";

            if (seen.Add(Normalize(screen)))
                result.Add(screen);
        }

        return result;
    }

    /// <summary>Bản đọc từ JSON đã lưu của <see cref="ReportScreens(IEnumerable{ReportMapRow})"/>.</summary>
    public static List<string> ReportScreens(string? json) => ReportScreens(Parse(json));

    /// <summary>
    /// Khối ngữ cảnh gắn vào MỌI lượt chat sau khi bảng đã chốt, vào lượt distill bản đồ bao phủ, và vào
    /// prompt sinh AI Design Spec. Không có khối này thì bảng chỉ là một màn bấm đẹp: BA vẫn hỏi lại đúng
    /// các báo cáo người dùng vừa duyệt. Trả null khi chưa chốt bảng.
    ///
    /// <para>
    /// <b>Bỏ tích SẠCH vẫn ra khối</b>, khác <see cref="FlowMapBuilder.RenderConfirmedBlock"/> — và khác là
    /// đúng: ở bảng luồng, không bước nào được giữ nghĩa là chẳng có gì để khẳng định, còn ở đây "ứng dụng
    /// không cần báo cáo nào" là một QUYẾT ĐỊNH của người dùng, và là quyết định dễ bị hỏi lại nhất nếu
    /// không nói ra. Trả null ở ca đó là để BA đề xuất lại đúng danh sách vừa bị tắt.
    /// </para>
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        var all = Parse(json);
        if (all.Count == 0)
            return null;

        var kept = all.Where(r => r.Included).ToList();
        if (kept.Count == 0)
            return "\n--- Bảng báo cáo / thống kê đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại) ---\n"
                + "Người dùng đã rà danh sách báo cáo và bỏ HẾT: ứng dụng KHÔNG cần báo cáo/thống kê nào. "
                + "Đây là quyết định của họ — KHÔNG đề xuất lại, KHÔNG hỏi lại nhóm «Báo cáo / thống kê».";

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng báo cáo / thống kê đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại) ---");
        sb.AppendLine("Mỗi dòng: tên báo cáo · trả lời câu hỏi gì · lấy số từ đối tượng nào · gộp/lọc theo gì.");

        foreach (var row in kept)
            sb.AppendLine("- " + RenderRow(row));

        // Báo cáo bị loại phải được NÓI RA ở đây, đúng như ở tin nhắn kể lại: thiếu nó thì lượt sau BA thấy
        // một nhu cầu người dùng từng nhắc mà bảng không có, và nó đề xuất lại đúng thứ vừa bị tắt.
        var dropped = all.Where(r => !r.Included).Select(r => r.Report.Trim()).ToList();
        if (dropped.Count > 0)
            sb.AppendLine("KHÔNG cần các báo cáo sau (người dùng đã bỏ): " + string.Join("; ", dropped));

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Tin nhắn mà TRÌNH DUYỆT gửi tiếp vào khung chat sau khi bảng đã lưu — bảng đi vào hội thoại bằng cửa
    /// của người dùng, y như bốn bảng kia. Soạn ở server (chứ không ghép trong JS) vì nó phải khớp đúng
    /// bảng đã được chuẩn hoá VÀ lưu: hai bản lệch nhau thì hội thoại kể một đằng còn dữ liệu dự án ghi một
    /// nẻo, mà mọi tầng chắt lọc đều tin vào bản kể.
    /// </summary>
    public static string RenderUserMessage(IReadOnlyList<ReportMapRow> rows)
    {
        if (rows.Count == 0)
            return string.Empty;

        var kept = rows.Where(r => r.Included).ToList();
        var sb = new StringBuilder();

        sb.AppendLine(kept.Count > 0
            ? "Mình đã rà bảng báo cáo / thống kê, đây là các báo cáo ứng dụng cần có:"
            : "Mình đã rà bảng báo cáo / thống kê: ứng dụng không cần báo cáo nào trong danh sách đó.");

        foreach (var row in kept)
            sb.AppendLine("- " + RenderRow(row));

        var dropped = rows.Where(r => !r.Included).Select(r => r.Report.Trim()).ToList();
        if (dropped.Count > 0)
            sb.AppendLine("- (bỏ: " + string.Join("; ", dropped) + ")");

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
            // ra "để để biết…" ở cả tin nhắn kể lại lẫn khối ngữ cảnh — thứ người dùng đọc thấy ngay.
            var question = row.Question.Trim();
            parts.Add(question.StartsWith("để", StringComparison.OrdinalIgnoreCase) ? question : "để " + question);
        }
        if (!string.IsNullOrWhiteSpace(row.Source))
            parts.Add("lấy số từ: " + row.Source.Trim());
        if (!string.IsNullOrWhiteSpace(row.Breakdown))
            parts.Add("gộp/lọc theo: " + row.Breakdown.Trim());
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Ô "lấy số từ" chỉ giữ khi nó gọi đúng tên một đối tượng của bảng đối tượng đã chốt. So khớp nới tay
    /// theo cụm CHỨA NHAU (cùng phép so của các bảng khác): model viết "Đơn đăng ký khóa học" cho đối tượng
    /// "Đơn đăng ký" không phải một nguồn bịa, chỉ là một cách gọi dài hơn — chuẩn hoá về đúng tên của bảng
    /// đối tượng để hai bảng còn nối được với nhau ở bước sinh spec.
    /// </summary>
    private static string MatchEntity(string? source, IReadOnlyList<string>? entityNames)
    {
        var value = (source ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        // Chưa chốt bảng đối tượng ⇒ không có gì để đối chiếu. Giữ nguyên chữ của model còn hơn xoá sạch
        // một cột mà người dùng sẽ thấy trống trơn ở mọi dòng.
        if (entityNames == null || entityNames.Count == 0)
            return Clip(value, MaxTextChars);

        var needle = Normalize(value);
        var match = entityNames.FirstOrDefault(e =>
        {
            var name = Normalize(e ?? string.Empty);
            return name.Length > 0 && (name == needle || needle.Contains(name) || name.Contains(needle));
        });

        return match == null ? string.Empty : Clip(match.Trim(), MaxTextChars);
    }

    private static string Normalize(string value)
        => string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.', ',', ':', ';', '-', '–');

    private static string Clip(string value, int max)
        => value.Length > max ? value[..max] : value;
}
