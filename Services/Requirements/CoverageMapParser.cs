using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Tiến độ của panel "Tiến độ khai thác". MẪU SỐ LÀ TỔNG SỐ NHÓM, không phải số nhóm áp dụng: nhóm bị
/// đánh [KHÔNG ÁP DỤNG] giữa chừng mà rút khỏi mẫu số thì con số đang chạy tự nhảy lùi mốc ("0/12" →
/// "3/9") và người dùng không hiểu vì sao thước đo đổi.
/// </summary>
/// <param name="Clear">Số nhóm đã [RÕ] — tử số của dòng chữ.</param>
/// <param name="Applicable">Số nhóm còn áp dụng (bỏ [KHÔNG ÁP DỤNG]).</param>
/// <param name="Total">Tổng số nhóm của bản đồ (12 với bản đồ đủ) — mẫu số của dòng chữ.</param>
public readonly record struct CoverageProgress(int Clear, int Applicable, int Total)
{
    /// <summary>Số nhóm đã được loại khỏi phạm vi dự án ([KHÔNG ÁP DỤNG]).</summary>
    public int NotApplicable => Total - Applicable;

    /// <summary>
    /// Phần trăm cho thanh tiến độ. Nhóm [KHÔNG ÁP DỤNG] tính là ĐÃ XONG — chúng không bao giờ lên [RÕ]
    /// được, nên nếu không tính thì thanh không bao giờ đầy trong khi cổng readiness (mọi dòng áp dụng
    /// [RÕ] — xem <see cref="RequirementReadinessGate"/>) đã mở nút "Write Requirement". Bất biến
    /// "thanh đầy ⇔ nút mở khoá" là thứ cả UI lẫn tài liệu đang dựa vào, phải giữ.
    /// </summary>
    public int Percent => Total == 0 ? 0 : (Clear + NotApplicable) * 100 / Total;
}

/// <summary>
/// Đọc/ghi "Bản đồ bao phủ yêu cầu" của một dự án. Bản đồ được LƯU dưới dạng JSON
/// (<see cref="CoverageMapDocument"/>) và mọi tầng đọc nó qua <see cref="Parse"/> để lấy danh sách
/// <see cref="CoverageMapItem"/> — panel tiến độ, cổng readiness, các cổng bảng, và năm guard sửa bản đồ.
///
/// <para>
/// <b>Vì sao là JSON.</b> Bản đồ từng là 12 dòng bullet nhồi bốn trường vào một chuỗi
/// (<c>- ★ Nhãn: [TRẠNG THÁI] đã ghi nhận còn thiếu: phần hụt {nguồn: trích}</c>). Mọi tầng muốn sửa
/// một phần đều phải regex ra rồi ghép chuỗi lại — bốn guard làm đúng thế, mỗi cái tự dựng lại cờ ★ và
/// khối <c>{nguồn: …}</c> theo cách riêng, và một cái quên thì bản đồ mất bằng chứng trong im lặng.
/// Trường bậc nhất khiến các guard chỉ còn gán thuộc tính; xem <see cref="CoverageMapItem"/>.
/// </para>
///
/// <para>
/// <b>Chỉ đọc JSON.</b> Đây là parser của bản đồ ĐÃ LƯU, nên nó không cần biết gì về văn xuôi model trả
/// về: lượt distill lấy JSON qua structured output, và nhánh dự phòng khi model không nhận
/// <c>response_format</c> nằm ở <c>RequirementCoverageService</c>, dùng <c>LlmJson</c> như mọi đường
/// "parse tay" khác của repo. Chiều ngược lại — dựng 12 dòng bullet cho prompt và bản xuất — là
/// <see cref="ToText"/>.
/// </para>
///
/// <para>
/// Chịu lỗi: JSON hỏng ⇒ danh sách rỗng (panel ẩn, cổng readiness báo "chưa tổng hợp được bản đồ").
/// Không ném lên khung chat.
/// </para>
/// </summary>
public static class CoverageMapParser
{
    /// <summary>
    /// Không escape non-ASCII: bản đồ toàn tiếng Việt, mà mặc định của System.Text.Json biến mỗi chữ có
    /// dấu thành <c>\uXXXX</c> — dài gấp ~6 lần và bản đồ này đi vào prompt ở MỌI lượt chat.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>Đọc bản đồ đã lưu thành danh sách dòng. Rỗng/không đọc được ⇒ danh sách rỗng.</summary>
    public static IReadOnlyList<CoverageMapItem> Parse(string? coverageMap)
    {
        if (string.IsNullOrWhiteSpace(coverageMap))
            return Array.Empty<CoverageMapItem>();

        var items = ToItems(LlmJson.TryDeserialize<CoverageMapDocument>(coverageMap));
        return AdoptLegacyGap(items, coverageMap);
    }

    /// <summary>
    /// Đường NÂNG CẤP cho các bản đồ đã nằm trong DB từ trước khi trường <c>gap</c> được đổi tên thành
    /// <see cref="CoverageMapItem.NextQuestion"/>. Không có nó thì mọi dự án đang phỏng vấn dở mất sạch câu
    /// hỏi kế tiếp ở lượt đầu tiên sau khi triển khai: cổng "Write Requirement" rơi hết về nhánh phát lại
    /// (<c>RequirementReadinessGate</c>) và người dùng nhận một câu hỏi rộng hơn hẳn câu họ đang chờ.
    /// <para>
    /// Chỉ chạy khi chuỗi có chữ <c>"gap"</c> — với bản đồ mới thì đây là một phép tìm chuỗi, không phải
    /// một lượt đọc JSON thứ hai; mà bản đồ này được đọc ở MỌI lượt chat. Đọc theo VỊ TRÍ và chỉ điền vào
    /// ô đang rỗng: một bản đồ vừa có <c>nextQuestion</c> vừa còn <c>gap</c> (dự án đang chuyển tiếp) thì
    /// trường mới thắng.
    /// </para>
    /// </summary>
    private static IReadOnlyList<CoverageMapItem> AdoptLegacyGap(IReadOnlyList<CoverageMapItem> items, string coverageMap)
    {
        if (items.Count == 0 || !coverageMap.Contains("\"gap\"", StringComparison.OrdinalIgnoreCase))
            return items;

        var legacy = LlmJson.TryDeserialize<LegacyCoverageMapDocument>(coverageMap)?.Items;
        if (legacy == null)
            return items;

        for (var i = 0; i < items.Count && i < legacy.Count; i++)
        {
            if (items[i].NextQuestion.Length == 0)
                items[i].NextQuestion = (legacy[i].Gap ?? string.Empty).Trim();
        }

        return items;
    }

    /// <summary>Hình dạng CŨ của bản đồ, chỉ để đọc lại trường <c>gap</c> đã đổi tên. Không bao giờ ghi.</summary>
    private sealed class LegacyCoverageMapDocument
    {
        public List<LegacyCoverageMapEntry> Items { get; set; } = new();
    }

    private sealed class LegacyCoverageMapEntry
    {
        public string? Gap { get; set; }
    }

    /// <summary>
    /// Chuẩn hoá một <see cref="CoverageMapDocument"/> (đọc từ DB hoặc do structured output trả về) thành
    /// các dòng bản đồ: trim, chuẩn hoá trạng thái, bỏ dòng không có nhãn. Mở cho
    /// <c>RequirementCoverageService</c> để đường structured output và đường đọc DB dùng CHUNG một bộ
    /// chuẩn hoá — hai bản sao là hai thứ trôi lệch nhau.
    /// </summary>
    public static IReadOnlyList<CoverageMapItem> ToItems(CoverageMapDocument? doc)
    {
        if (doc?.Items == null)
            return Array.Empty<CoverageMapItem>();

        return doc.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.Label))
            .Select(x => new CoverageMapItem
            {
                Label = x.Label.Trim(),
                IsCore = x.Core,
                Status = NormalizeStatus(x.Status),
                Known = (x.Known ?? string.Empty).Trim(),
                NextQuestion = (x.NextQuestion ?? string.Empty).Trim(),
                Evidence = (x.Evidence ?? string.Empty).Trim()
            })
            .ToList();
    }

    /// <summary>Ghi danh sách dòng thành JSON để lưu vào <c>Project.RequirementCoverageMap</c>.</summary>
    public static string Serialize(IReadOnlyList<CoverageMapItem> items) =>
        JsonSerializer.Serialize(new CoverageMapDocument
        {
            Items = items.Select(x => new CoverageMapEntry
            {
                Label = x.Label,
                Core = x.IsCore,
                Status = x.Status,
                Known = x.Known,
                NextQuestion = x.NextQuestion,
                Evidence = x.Evidence
            }).ToList()
        }, SerializerOptions);

    /// <summary>
    /// Dựng lại bản đồ ở dạng 12 dòng bullet cho NGƯỜI và cho MODEL đọc: ngữ cảnh chat của BA, bản xuất
    /// hội thoại. JSON là format lưu trữ vì nó sửa được từng trường, nhưng nhét dấu ngoặc nhọn vào prompt
    /// chat thì vừa tốn token vừa mời model chép lại cú pháp JSON ra câu trả lời cho người dùng.
    /// </summary>
    public static string ToText(IReadOnlyList<CoverageMapItem> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.Append("- ");
            if (item.IsCore)
                sb.Append("★ ");
            sb.Append(item.Label).Append(": [").Append(item.Status).Append(']');

            if (!string.IsNullOrWhiteSpace(item.Summary))
                sb.Append(' ').Append(item.Summary);
            if (!string.IsNullOrWhiteSpace(item.Evidence))
                sb.Append(" {nguồn: ").Append(item.Evidence).Append('}');

            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Tiến độ khai thác của bản đồ — cho thanh + dòng "Đã rõ x/y nhóm" của panel.</summary>
    public static CoverageProgress Progress(IReadOnlyList<CoverageMapItem> items) => new(
        Clear: items.Count(x => x.Status == "RÕ"),
        Applicable: items.Count(x => x.Status != "KHÔNG ÁP DỤNG"),
        Total: items.Count);

    /// <summary>Chuẩn hoá tên trạng thái của một dòng; giá trị lạ ⇒ [CHƯA HỎI].</summary>
    private static string NormalizeStatus(string? raw)
    {
        var status = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return status switch
        {
            "RÕ" or "RO" => "RÕ",
            "MỘT PHẦN" or "MOT PHAN" => "MỘT PHẦN",
            "KHÔNG ÁP DỤNG" or "KHONG AP DUNG" => "KHÔNG ÁP DỤNG",
            _ => "CHƯA HỎI"
        };
    }
}
