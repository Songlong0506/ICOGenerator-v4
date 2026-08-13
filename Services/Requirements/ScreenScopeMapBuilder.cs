using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng màn hình" — danh sách màn hình/tính năng dự kiến của ứng dụng để người dùng rà
/// trước khi nó thành nền cho mọi thứ phía sau (xem <see cref="ScreenScopeRow"/>).
///
/// <para>
/// Cùng ba chốt chặn tất định với <see cref="PermissionMatrixBuilder"/> — và chúng quan trọng hơn ở đây,
/// vì bảng này là thứ bảng phân quyền sẽ đứng lên:
/// </para>
/// <list type="bullet">
///   <item><b>Màn hình bịa.</b> Mọi dòng phải khớp một mục <c>Project.PlannedScope</c>, và luôn lấy lại
///   đúng chữ của PlannedScope chứ không chữ của model.</item>
///   <item><b>Màn hình bị bỏ quên.</b> Mục phạm vi model không nhắc tới vẫn được BỔ SUNG vào cuối bảng —
///   ở trạng thái TÍCH SẴN như mọi dòng khác, vì "BA quên nêu" không phải "người dùng đã loại". Bỏ nó đi
///   là ra một quyết định thay người dùng ở đúng chỗ họ không nhìn thấy để phản đối.</item>
///   <item><b>Bước luồng không màn hình nào phụ trách.</b> Chốt chặn RIÊNG của bảng này và là lý do
///   <see cref="ScreenScopeRow.FlowSteps"/> tồn tại — xem <see cref="UncoveredActions"/>.</item>
/// </list>
/// </summary>
public static class ScreenScopeMapBuilder
{
    /// <summary>Trần số màn hình. Cùng trần với số dòng phạm vi mà PermissionMatrixBuilder chấp nhận.</summary>
    public const int MaxRows = 40;

    /// <summary>Trần số bước luồng gắn cho MỘT màn hình — nhiều hơn là dấu hiệu model dán cả luồng vào một dòng.</summary>
    public const int MaxFlowStepsPerScreen = 8;

    private const int MaxTextChars = 200;
    private const int MaxEvidenceChars = 300;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Bảng cuối cùng cho lượt BA BÀY BẢNG: giữ các dòng khớp phạm vi đã chắt, bỏ dòng bịa/trùng, bổ sung
    /// mọi màn hình chưa được nhắc tới, và luôn xếp theo thứ tự của <paramref name="plannedScope"/>. Trả
    /// rỗng khi phạm vi trống — không có phạm vi thì bảng không có gì để hỏi.
    ///
    /// <para>
    /// Khác <see cref="Sanitize"/> ở đúng chỗ cờ <c>included</c>: ở đây mọi dòng ra TÍCH SẴN bất kể model
    /// trả gì, vì cờ đó là chỗ NGƯỜI DÙNG loại một màn hình, không phải chỗ model tự phủ nhận đề xuất của
    /// mình. Structured output buộc điền đủ trường, nên một model điền <c>false</c> cho có sẽ bỏ tích sạch
    /// bảng và người dùng gửi đi một phạm vi RỖNG trong khi tưởng mình vừa xác nhận cả ứng dụng.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> Build(IEnumerable<ScreenScopeRow>? proposed, IReadOnlyList<string> plannedScope)
        => BuildCore(proposed, plannedScope, respectIncluded: false);

    /// <summary>
    /// Bản chuẩn hoá cho dữ liệu ĐẾN TỪ TRÌNH DUYỆT. Server không tin bảng client gửi kể cả khi chính nó
    /// vừa render ra: tên màn hình vẫn phải khớp lại phạm vi đã chắt. Khác <see cref="Build"/>: giữ đúng
    /// lựa chọn tích/bỏ tích của người dùng, và xoá cờ khóa (bảng đã gửi thì mọi dòng là quyết định của họ).
    /// </summary>
    public static List<ScreenScopeRow> Sanitize(IEnumerable<ScreenScopeRow>? submitted, IReadOnlyList<string> plannedScope)
    {
        var rows = BuildCore(submitted, plannedScope, respectIncluded: true);
        foreach (var row in rows)
        {
            row.Locked = false;
            row.Evidence = string.Empty;
        }
        return rows;
    }

    private static List<ScreenScopeRow> BuildCore(
        IEnumerable<ScreenScopeRow>? proposed,
        IReadOnlyList<string> plannedScope,
        bool respectIncluded)
    {
        var screens = CleanScreens(plannedScope);
        if (screens.Count == 0)
            return new List<ScreenScopeRow>();

        var byScreen = new Dictionary<string, ScreenScopeRow>(StringComparer.Ordinal);
        foreach (var row in proposed ?? Enumerable.Empty<ScreenScopeRow>())
        {
            if (row == null)
                continue;

            var screen = MatchScreen(row.Screen, screens);
            if (screen == null || byScreen.ContainsKey(screen))
                continue;

            var evidence = Clip((row.Evidence ?? string.Empty).Trim(), MaxEvidenceChars);
            byScreen[screen] = new ScreenScopeRow
            {
                Screen = screen, // chữ của PHẠM VI ĐÃ CHẮT, không phải chữ của model
                Purpose = Clip((row.Purpose ?? string.Empty).Trim(), MaxTextChars),
                Functions = Clip((row.Functions ?? string.Empty).Trim(), MaxTextChars),
                FlowSteps = CleanFlowSteps(row.FlowSteps),
                Included = !respectIncluded || row.Included,
                // LUẬT BẰNG CHỨNG — cờ suông không khóa được dòng nào. Xem PermissionGrant.Locked.
                Locked = evidence.Length > 0,
                Evidence = evidence
            };
        }

        var result = new List<ScreenScopeRow>();
        foreach (var screen in screens)
        {
            result.Add(byScreen.TryGetValue(screen, out var found)
                ? found
                // Màn hình model bỏ quên: vẫn phải có mặt, TÍCH SẴN. Đưa vào ở trạng thái bỏ tích là ra
                // quyết định loại thay người dùng, còn bỏ hẳn là làm nó biến mất khỏi mọi tầng sau.
                : new ScreenScopeRow { Screen = screen, Included = true });

            if (result.Count >= MaxRows)
                break;
        }

        return result;
    }

    /// <summary>Đọc JSON bảng màn hình đã lưu (cột DB hoặc payload client). null/rỗng/hỏng ⇒ mảng rỗng.</summary>
    public static List<ScreenScopeRow> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<ScreenScopeRow>();

        try
        {
            var rows = JsonSerializer.Deserialize<List<ScreenScopeRow>>(json, ReadOptions) ?? new List<ScreenScopeRow>();
            return rows.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Screen)).ToList();
        }
        catch
        {
            return new List<ScreenScopeRow>();
        }
    }

    /// <summary>Dự án này đã chốt bảng màn hình chưa.</summary>
    public static bool IsConfirmed(string? json) => Parse(json).Count > 0;

    /// <summary>
    /// PHẠM VI MÀN HÌNH THẬT SỰ của dự án — nguồn dòng cho bảng phân quyền và cho mục
    /// <c>## 6. Screens To Generate</c> của spec.
    ///
    /// <para>
    /// Chưa chốt bảng ⇒ trả nguyên <paramref name="plannedScope"/>, tức mọi thứ chạy đúng như trước khi có
    /// tính năng này. Đã chốt ⇒ các dòng người dùng GIỮ, cộng những mục phạm vi mới lộ ra SAU lúc chốt.
    /// Mục mới phải được thêm vào (buổi phỏng vấn còn tiếp tục sau khi bảng đã chốt, và một màn hình lộ ra
    /// ở lượt sau mà không vào được bảng phân quyền thì mặc nhiên "không ai được xem"); còn mục người dùng
    /// đã BỎ TÍCH thì không bao giờ quay lại, kể cả khi nó vẫn nằm trong PlannedScope — lượt chắt lọc
    /// PlannedScope không đọc bảng, nên nó sẽ giữ nguyên mục đó mãi, và mở lại thứ họ vừa đóng là đúng lỗi
    /// mà bảng cột đã cấm.
    /// </para>
    /// </summary>
    public static List<string> EffectiveScreens(string? screenScopeJson, IReadOnlyList<string> plannedScope)
    {
        var rows = Parse(screenScopeJson);
        if (rows.Count == 0)
            return CleanScreens(plannedScope);

        var kept = rows.Where(r => r.Included).ToList();
        // KHÔNG dòng nào được giữ ⇒ coi là bảng hỏng, KHÔNG phải "ứng dụng không có màn hình nào". Trả
        // rỗng ở đây là khóa chết cả tuyến trong im lặng: cổng bảng phân quyền đòi phạm vi có mục mới mở,
        // mà dòng phân quyền chỉ [RÕ] sau khi bảng đó chốt ⇒ nút "Write Requirement" không bao giờ sáng và
        // không có gì trên màn hình nói vì sao. Cùng luật fail-open với bảng cột không khớp hàng tiêu đề
        // nào: để lọt vài mục thừa rẻ hơn nhiều so với cắt sạch.
        if (kept.Count == 0)
            return CleanScreens(plannedScope);

        var result = kept.Select(r => r.Screen.Trim()).ToList();
        var known = new HashSet<string>(rows.Select(r => Normalize(r.Screen)), StringComparer.Ordinal);

        foreach (var raw in CleanScreens(plannedScope))
        {
            if (known.Add(Normalize(raw)))
                result.Add(raw);
        }

        return result;
    }

    /// <summary>
    /// Các BƯỚC LUỒNG đã chốt mà KHÔNG màn hình nào trong bảng nhận phụ trách — phép kiểm tất định của
    /// tính năng, chạy bằng code chứ không bằng một lời gọi LLM nữa.
    ///
    /// <para>
    /// Vì sao nó đáng có: hai danh sách này đọc riêng đều "đạt" — bảng luồng đầy đủ, bảng màn hình đầy đủ —
    /// còn chỗ hỏng nằm ở mối nối giữa chúng, đúng loại lỗi đắt nhất của cả dây chuyền. Một bước không màn
    /// hình nào phụ trách nghĩa là hoặc người dùng sẽ không có chỗ nào để làm bước đó, hoặc bước đó không
    /// có thật. Cả hai đều phải hỏi, và hỏi ngay lúc bảng còn trên màn hình rẻ hơn hẳn hỏi lại ở POC.
    /// </para>
    ///
    /// <para>
    /// So khớp bằng CHỨA-NHAU sau chuẩn hoá chứ không khớp chính xác: người dùng sửa ô "bước phục vụ" bằng
    /// lời của họ, và một phép so nguyên văn sẽ báo động giả ở gần như mọi dòng — mà một cảnh báo luôn sai
    /// thì lần thứ hai không ai đọc nữa.
    /// </para>
    /// </summary>
    public static List<string> UncoveredActions(IReadOnlyList<ScreenScopeRow> rows, string? flowMapJson)
    {
        var actions = FlowMapBuilder.IncludedActions(flowMapJson);
        if (actions.Count == 0)
            return new List<string>();

        var covered = rows
            .Where(r => r.Included)
            .SelectMany(r => r.FlowSteps)
            .Select(Normalize)
            .Where(s => s.Length > 0)
            .ToList();

        return actions
            .Where(action =>
            {
                var key = Normalize(action);
                return key.Length > 0
                       && !covered.Any(c => c.Contains(key, StringComparison.Ordinal) || key.Contains(c, StringComparison.Ordinal));
            })
            .ToList();
    }

    /// <summary>
    /// Khối ngữ cảnh gắn vào MỌI lượt chat sau khi bảng đã chốt, vào lượt distill bản đồ bao phủ, và vào
    /// prompt sinh AI Design Spec. Trả null khi chưa chốt.
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        var rows = Parse(json);
        if (rows.Count == 0)
            return null;

        var kept = rows.Where(r => r.Included).ToList();
        var dropped = rows.Where(r => !r.Included).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng màn hình đã được NGƯỜI DÙNG CHỐT (phạm vi màn hình của ứng dụng) ---");
        sb.AppendLine("Đây là TOÀN BỘ màn hình của ứng dụng. KHÔNG thêm màn hình mới ngoài danh sách này, và "
            + "KHÔNG hỏi lại việc của từng màn.");

        foreach (var row in kept)
        {
            var purpose = string.IsNullOrWhiteSpace(row.Purpose) ? string.Empty : $" — {row.Purpose}";
            sb.AppendLine($"* {row.Screen}{purpose}");
            if (!string.IsNullOrWhiteSpace(row.Functions))
                sb.AppendLine($"  - chức năng: {row.Functions}");
            if (row.FlowSteps.Count > 0)
                sb.AppendLine($"  - phục vụ bước: {string.Join("; ", row.FlowSteps)}");
        }

        if (dropped.Count > 0)
            sb.AppendLine("Màn hình người dùng đã LOẠI (đừng dựng, đừng nhắc lại): "
                + string.Join(", ", dropped.Select(r => r.Screen)) + ".");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Tin nhắn mà TRÌNH DUYỆT gửi tiếp vào khung chat sau khi bảng đã lưu — cùng khuôn hai bước với bảng
    /// cột và bảng phân quyền, và soạn ở server vì cùng lý do: bản kể phải khớp đúng bản đã lưu.
    /// </summary>
    public static string RenderUserMessage(IReadOnlyList<ScreenScopeRow> rows)
    {
        if (rows.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Mình đã rà bảng màn hình, đây là các màn hình ứng dụng cần có:");
        sb.AppendLine();

        foreach (var row in rows.Where(r => r.Included))
        {
            var purpose = string.IsNullOrWhiteSpace(row.Purpose) ? string.Empty : $" — {row.Purpose}";
            sb.AppendLine($"- {row.Screen}{purpose}"
                + (string.IsNullOrWhiteSpace(row.Functions) ? string.Empty : $" [chức năng: {row.Functions}]"));
        }

        // Màn hình bị loại phải được NÓI RA — cùng lý do bảng cột gọi tên cả cột bị bỏ tích: im lặng thì
        // người dùng không có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại.
        var dropped = rows.Where(r => !r.Included).ToList();
        if (dropped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Các màn hình mình KHÔNG cần: " + string.Join(", ", dropped.Select(r => r.Screen)) + ".");
        }

        return sb.ToString().TrimEnd();
    }

    // ==== chuẩn hoá từng phần ====

    private static List<string> CleanScreens(IReadOnlyList<string>? plannedScope)
    {
        var result = new List<string>();
        if (plannedScope == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in plannedScope)
        {
            var screen = (raw ?? string.Empty).Trim();
            if (screen.Length == 0 || !seen.Add(Normalize(screen)))
                continue;

            result.Add(Clip(screen, MaxTextChars));
            if (result.Count >= MaxRows)
                break;
        }
        return result;
    }

    private static List<string> CleanFlowSteps(IEnumerable<string>? proposed)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in proposed ?? Enumerable.Empty<string>())
        {
            var step = (raw ?? string.Empty).Trim();
            if (step.Length == 0 || !seen.Add(Normalize(step)))
                continue;

            result.Add(Clip(step, MaxTextChars));
            if (result.Count >= MaxFlowStepsPerScreen)
                break;
        }
        return result;
    }

    // Cùng phép ghép tên màn hình với PermissionMatrixBuilder (khớp chính xác trước, rồi cho phép một bên
    // chứa bên kia khi model rút gọn tên), và cùng ngưỡng độ dài để những mẩu quá ngắn không dính vào mọi
    // mục. Mơ hồ (nhiều mục cùng khớp) ⇒ bỏ hẳn: gán bừa là đặt cả một màn hình lên nhầm dòng.
    private const int MinContainsLength = 8;

    private static string? MatchScreen(string? proposed, IReadOnlyList<string> screens)
    {
        var value = Normalize(proposed ?? string.Empty);
        if (value.Length == 0)
            return null;

        foreach (var screen in screens)
        {
            if (Normalize(screen) == value)
                return screen;
        }

        if (value.Length < MinContainsLength)
            return null;

        var matches = screens.Where(s =>
        {
            var normalized = Normalize(s);
            return normalized.Contains(value, StringComparison.Ordinal)
                || (normalized.Length >= MinContainsLength && value.Contains(normalized, StringComparison.Ordinal));
        }).ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Normalize(string value)
        => string.Join(' ', (value ?? string.Empty).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.', ',', ':', ';', '-', '–');

    private static string Clip(string value, int max)
        => value.Length > max ? value[..max] : value;
}
