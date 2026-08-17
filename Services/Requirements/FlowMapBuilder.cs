using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng luồng nghiệp vụ" — các luồng của ứng dụng theo vai trò, mỗi luồng là một chuỗi
/// bước sửa được và bỏ được (xem <see cref="FlowMapRow"/>).
///
/// <para>
/// Cùng vai trò chốt chặn TẤT ĐỊNH với <see cref="PermissionMatrixBuilder"/> và
/// <see cref="SourceColumnMapBuilder"/>, và vá ba đường hỏng cùng họ:
/// </para>
/// <list type="bullet">
///   <item><b>Luồng không có ngoại lệ nào.</b> Model luôn có xu hướng chỉ vẽ đường đi thuận — đó là thứ
///   người dùng kể, và cũng là thứ dễ viết. Nhưng chuẩn <c>[RÕ]</c> của nhóm «Luồng ngoại lệ» đòi một tình
///   huống hỏng cụ thể kèm cách xử lý, nên một bảng toàn luồng chính là bảng bỏ trống đúng nửa đắt nhất.
///   <see cref="Build"/> vì vậy CẮT bớt luồng chính khi vượt trần nhưng không bao giờ cắt ngoại lệ trước.</item>
///   <item><b>Bước không có ai làm.</b> Một bước thiếu <c>actor</c> đọc lên như một sự kiện tự xảy ra, và
///   phần "ai làm bước nào" — thứ quyết định cả phân quyền lẫn thông báo — biến mất mà không ai thấy.
///   Bước thiếu vai được gán về vai chủ luồng.</item>
///   <item><b>Luồng một bước.</b> Một "luồng" chỉ có một bước không phải luồng mà là một câu mô tả; nó
///   không kiểm được bằng oracle và cũng không cho người dùng chỗ nào để bắt lỗi thứ tự. Bị loại.</item>
/// </list>
///
/// <para>
/// <b>Không có luật bằng chứng ở bảng này.</b> Bước từng khóa được bằng trích dẫn như bảng phân quyền, và
/// đó là một đường hỏng: mọi bước ra khỏi đây đều được GIỮ sẵn, nên trích dẫn không đổi trạng thái nào mà
/// chỉ khóa cứng ô — model kèm trích dẫn cho tất cả (nó luôn kèm được) là cả bảng thành chỉ-đọc ở đúng
/// chiều người dùng cần bác, tức đúng cái chip "Đồng ý phương án này" phóng to mà luật ấy sinh ra để chặn.
/// Bỏ bước sai nay là nút <b>×</b> trên từng dòng, không cửa nào khóa được. Xem
/// <c>docs/requirement-flow.md</c>, mục "Vì sao bảng luồng và bảng màn hình không có dấu ✓ bằng chứng".
/// </para>
/// </summary>
public static class FlowMapBuilder
{
    /// <summary>Trần số luồng. Nhiều hơn thì bảng không rà nổi trong một lượt, và phần thừa luôn là luồng bịa.</summary>
    public const int MaxFlows = 6;

    /// <summary>Trần số luồng NGOẠI LỆ. Prompt xin 1–2; trần rộng hơn một chút để không cắt oan.</summary>
    public const int MaxExceptionFlows = 3;

    /// <summary>Trần số bước một luồng. Dài hơn là bản mô tả thao tác chứ không còn là luồng nghiệp vụ.</summary>
    public const int MaxStepsPerFlow = 10;

    /// <summary>Ít hơn ngần này bước thì không phải một luồng — xem ghi chú class.</summary>
    public const int MinStepsPerFlow = 2;

    private const int MaxTextChars = 200;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Bảng cuối cùng cho lượt BA BÀY BẢNG: bỏ luồng rỗng/một bước/trùng tên, kéo <c>kind</c> về hai giá
    /// trị hợp lệ, gán vai chủ luồng cho bước thiếu vai, rồi xếp luồng CHÍNH trước, ngoại lệ sau. Trả rỗng
    /// khi không đề xuất nào dùng được — caller fail-open, lượt chạy như một lượt chat thường và cổng mở
    /// lại ở lượt sau.
    ///
    /// <para>
    /// Mọi bước ra khỏi đây đều ở trạng thái ĐƯỢC GIỮ, bất kể model trả gì: cờ <c>included</c> là chỗ
    /// NGƯỜI DÙNG nói "bước này sai", không phải chỗ model tự phủ nhận đề xuất của chính nó — mà structured
    /// output thì luôn phải điền đủ trường, nên một model điền <c>false</c> cho có sẽ âm thầm loại sạch
    /// bảng và người dùng gửi đi một bảng rỗng nghĩ rằng mình vừa xác nhận cả luồng.
    /// </para>
    /// </summary>
    public static List<FlowMapRow> Build(IEnumerable<FlowMapRow>? proposed)
        => BuildCore(proposed, respectIncluded: false);

    private static List<FlowMapRow> BuildCore(IEnumerable<FlowMapRow>? proposed, bool respectIncluded)
    {
        var happy = new List<FlowMapRow>();
        var exceptions = new List<FlowMapRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in proposed ?? Enumerable.Empty<FlowMapRow>())
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Name))
                continue;

            var name = Clip(row.Name.Trim(), MaxTextChars);
            if (!seen.Add(Normalize(name)))
                continue;

            var role = Clip((row.Role ?? string.Empty).Trim(), MaxTextChars);
            var steps = NormalizeSteps(row.Steps, role, respectIncluded);
            if (steps.Count < MinStepsPerFlow)
                continue;

            var kind = FlowKind.Normalize(row.Kind);
            var built = new FlowMapRow
            {
                Name = name,
                Kind = kind,
                Role = role,
                // Điều kiện kích hoạt chỉ có nghĩa với ngoại lệ; giữ nó trên luồng chính là bày ra một ô
                // mà người dùng phải đoán xem mình nên điền gì.
                Trigger = kind == FlowKind.Exception ? Clip((row.Trigger ?? string.Empty).Trim(), MaxTextChars) : string.Empty,
                Steps = steps
            };

            if (kind == FlowKind.Exception)
            {
                if (exceptions.Count < MaxExceptionFlows)
                    exceptions.Add(built);
            }
            else
            {
                happy.Add(built);
            }
        }

        if (happy.Count == 0 && exceptions.Count == 0)
            return new List<FlowMapRow>();

        // Cắt ở trần TỔNG nhưng luôn giữ chỗ cho ngoại lệ: cắt tuần tự sẽ luôn cắt đúng phần cuối bảng, mà
        // ngoại lệ theo prompt nằm sau luồng chính — tức phần khó lấy nhất của cả buổi bị vứt đầu tiên.
        var roomForHappy = Math.Max(1, MaxFlows - exceptions.Count);
        var result = happy.Take(roomForHappy).ToList();
        result.AddRange(exceptions);
        return result;
    }

    /// <summary>
    /// Bản chuẩn hoá cho dữ liệu ĐẾN TỪ TRÌNH DUYỆT. Cùng luật với <see cref="Build"/> — server không tin
    /// bảng client gửi kể cả khi chính nó vừa render ra — khác đúng một điểm: đường này TÔN TRỌNG cờ
    /// <c>included</c>, vì ở đây nó là lựa chọn của người dùng chứ không phải của model.
    ///
    /// <para>
    /// Bước người dùng BỎ vẫn được giữ lại ở đây (không lọc): tin nhắn gửi vào hội thoại phải kể được rằng
    /// bước đó đã bị loại. Các đường tiêu thụ phía sau (khối ngữ cảnh, spec) mới là chỗ lọc.
    /// </para>
    /// </summary>
    public static List<FlowMapRow> Sanitize(IEnumerable<FlowMapRow>? submitted)
        => BuildCore(submitted, respectIncluded: true);

    /// <summary>Đọc JSON bảng luồng đã lưu (cột DB hoặc payload client). null/rỗng/hỏng ⇒ mảng rỗng.</summary>
    public static List<FlowMapRow> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<FlowMapRow>();

        try
        {
            var rows = JsonSerializer.Deserialize<List<FlowMapRow>>(json, ReadOptions) ?? new List<FlowMapRow>();
            return rows
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name) && r.Steps is { Count: > 0 })
                .ToList();
        }
        catch
        {
            return new List<FlowMapRow>();
        }
    }

    /// <summary>Dự án này đã chốt bảng luồng chưa.</summary>
    public static bool IsConfirmed(string? json) => Parse(json).Count > 0;

    /// <summary>
    /// Các bước ĐƯỢC GIỮ của một bảng đã chốt, phẳng thành danh sách hành động — nguyên liệu của phép kiểm
    /// "màn hình nào phụ trách bước nào" ở <see cref="ScreenScopeMapBuilder"/>.
    /// </summary>
    public static List<string> IncludedActions(string? json)
        => Parse(json)
            .SelectMany(r => r.Steps)
            .Where(s => s.Included && !string.IsNullOrWhiteSpace(s.Action))
            .Select(s => s.Action.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Khối ngữ cảnh gắn vào MỌI lượt chat sau khi bảng đã chốt, vào lượt distill bản đồ bao phủ, và vào
    /// prompt sinh AI Design Spec. Không có khối này thì bảng chỉ là một màn bấm đẹp: BA vẫn hỏi lại đúng
    /// các bước người dùng vừa duyệt, và spec vẫn tự ráp lại luồng từ văn xuôi. Trả null khi chưa chốt.
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        var rows = Parse(json)
            .Where(r => r.Steps.Any(s => s.Included))
            .ToList();
        // Mọi bước đều bị bỏ ⇒ không có gì để khẳng định. Trả về một khối chỉ gồm hai dòng tiêu đề là
        // nói dối cả hai chiều: BA đọc thấy "đã chốt" nên thôi hỏi lại luồng, còn nội dung thì trống.
        if (rows.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng luồng nghiệp vụ đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại các bước này) ---");
        sb.AppendLine("Mỗi luồng: tên · loại · vai trò khởi xướng, rồi các bước theo đúng thứ tự. Bước người dùng "
            + "ĐÃ BỎ không có trong danh sách dưới đây — KHÔNG nhắc lại chúng, đó là thứ họ vừa đóng.");

        foreach (var row in rows)
        {
            var steps = row.Steps.Where(s => s.Included).ToList();
            var role = string.IsNullOrWhiteSpace(row.Role) ? string.Empty : $" · vai: {row.Role}";
            var trigger = string.IsNullOrWhiteSpace(row.Trigger) ? string.Empty : $" · kích hoạt khi: {row.Trigger}";
            sb.AppendLine($"* {row.Name} [{row.Kind}]{role}{trigger}");
            foreach (var step in steps)
                sb.AppendLine("  - " + RenderStep(step));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Tin nhắn mà TRÌNH DUYỆT gửi tiếp vào khung chat sau khi bảng đã lưu — bảng đi vào hội thoại bằng cửa
    /// của người dùng, y như bảng cột và bảng phân quyền. Soạn ở server (chứ không ghép trong JS) vì nó
    /// phải khớp đúng bảng đã được chuẩn hoá VÀ lưu: hai bản lệch nhau thì hội thoại kể một đằng còn dữ
    /// liệu dự án ghi một nẻo, mà mọi tầng chắt lọc đều tin vào bản kể.
    /// </summary>
    public static string RenderUserMessage(IReadOnlyList<FlowMapRow> rows)
    {
        if (rows.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Mình đã rà bảng luồng nghiệp vụ, đây là các luồng đúng:");

        foreach (var row in rows)
        {
            sb.AppendLine();
            var role = string.IsNullOrWhiteSpace(row.Role) ? string.Empty : $" — {row.Role}";
            var trigger = string.IsNullOrWhiteSpace(row.Trigger) ? string.Empty : $" (khi {row.Trigger})";
            sb.AppendLine($"{row.Name} [{row.Kind}]{role}{trigger}:");

            var kept = row.Steps.Where(s => s.Included).ToList();
            if (kept.Count == 0)
                sb.AppendLine("- (không bước nào đúng — mình sẽ mô tả lại luồng này)");
            foreach (var step in kept)
                sb.AppendLine("- " + RenderStep(step));

            // Bước bị loại phải được NÓI RA. Im lặng bỏ đi thì người dùng không có bằng chứng nào cho thấy
            // mình vừa loại đúng thứ định loại — cùng lý do bảng cột gọi tên cả cột bị bỏ tích. Đây cũng là
            // lý do nút × chỉ ĐÁNH DẤU bỏ chứ không xóa dòng khỏi payload.
            var dropped = row.Steps.Where(s => !s.Included && !string.IsNullOrWhiteSpace(s.Action)).ToList();
            if (dropped.Count > 0)
                sb.AppendLine("- (bỏ: " + string.Join("; ", dropped.Select(s => s.Action.Trim())) + ")");
        }

        return sb.ToString().TrimEnd();
    }

    // ==== chuẩn hoá từng phần ====

    private static string RenderStep(FlowMapStep step)
    {
        var actor = string.IsNullOrWhiteSpace(step.Actor) ? string.Empty : step.Actor.Trim() + ": ";
        var outcome = string.IsNullOrWhiteSpace(step.Outcome) ? string.Empty : $" ⇒ {step.Outcome.Trim()}";
        return $"{actor}{step.Action.Trim()}{outcome}";
    }

    private static List<FlowMapStep> NormalizeSteps(IEnumerable<FlowMapStep>? proposed, string flowRole, bool respectIncluded)
    {
        var result = new List<FlowMapStep>();
        foreach (var step in proposed ?? Enumerable.Empty<FlowMapStep>())
        {
            if (step == null || string.IsNullOrWhiteSpace(step.Action))
                continue;

            result.Add(new FlowMapStep
            {
                // Bước thiếu vai đọc lên như một sự kiện tự xảy ra, và phần "ai làm bước nào" — thứ quyết
                // định cả phân quyền lẫn thông báo ở các bảng sau — biến mất mà không ai nhìn thấy để hỏi.
                Actor = Clip(string.IsNullOrWhiteSpace(step.Actor) ? flowRole : step.Actor.Trim(), MaxTextChars),
                Action = Clip(step.Action.Trim(), MaxTextChars),
                Outcome = Clip((step.Outcome ?? string.Empty).Trim(), MaxTextChars),
                // Lượt BÀY BẢNG: mọi bước được giữ (người dùng bấm × để BỎ bước sai — bắt họ tích từng
                // bước đúng là đổi một thao tác đính chính lấy mười thao tác xác nhận). Lượt GỬI: giữ đúng
                // thứ họ đã chọn. Xem ghi chú của Build.
                Included = !respectIncluded || step.Included
            });

            if (result.Count >= MaxStepsPerFlow)
                break;
        }
        return result;
    }

    private static string Normalize(string value)
        => string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.', ',', ':', ';', '-', '–');

    private static string Clip(string value, int max)
        => value.Length > max ? value[..max] : value;
}
