using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng và chuẩn hoá "bảng màn hình" — danh sách màn hình dự kiến của ứng dụng kèm các chức năng trên từng
/// màn, để người dùng rà trước khi nó thành nền cho mọi thứ phía sau (xem <see cref="ScreenScopeRow"/>).
///
/// <para>
/// Cùng ba chốt chặn tất định với <see cref="PermissionMatrixBuilder"/> — và chúng quan trọng hơn ở đây,
/// vì bảng này là thứ bảng phân quyền sẽ đứng lên:
/// </para>
/// <list type="bullet">
///   <item><b>Màn hình bịa.</b> Mọi dòng phải khớp một mục của danh sách cho phép, và luôn lấy lại đúng chữ
///   của danh sách đó chứ không chữ của model. Lượt BÀY BẢNG đối chiếu với phạm vi đã chắt
///   (<see cref="Build"/>); đường GỬI đối chiếu với chính bảng server đã render (<see cref="Sanitize"/>) —
///   hai danh sách khác nhau, và trộn chúng là lỗi câm, xem <c>ConfirmScreenScopeUseCase</c>.</item>
///   <item><b>Màn hình bị bỏ quên.</b> Mục phạm vi model không nhắc tới vẫn được BỔ SUNG vào cuối bảng —
///   ở trạng thái TÍCH SẴN như mọi dòng khác, vì "BA quên nêu" không phải "người dùng đã loại". Bỏ nó đi
///   là ra một quyết định thay người dùng ở đúng chỗ họ không nhìn thấy để phản đối. Ngoại lệ DUY NHẤT:
///   mục đã được một dòng khai là gộp vào mình qua <see cref="ScreenScopeRow.Covers"/> — xem
///   <see cref="CoveredScopeItems"/>.</item>
///   <item><b>Bước luồng không chức năng nào phụ trách.</b> Chốt chặn RIÊNG của bảng này và là lý do
///   <see cref="ScreenFunction.FlowSteps"/> tồn tại — xem <see cref="UncoveredActions"/>.</item>
/// </list>
/// </summary>
public static class ScreenScopeMapBuilder
{
    /// <summary>Trần số màn hình. Cùng trần với số dòng phạm vi mà PermissionMatrixBuilder chấp nhận.</summary>
    public const int MaxRows = 40;

    /// <summary>Trần số bước luồng gắn cho MỘT chức năng — nhiều hơn là dấu hiệu model dán cả luồng vào một dòng.</summary>
    public const int MaxFlowStepsPerFunction = 6;

    /// <summary>
    /// Trần số chức năng của MỘT màn hình. Một màn hình 20 chức năng không phải một màn hình được mô tả kỹ
    /// mà là hai màn hình bị gộp làm một — và bảng dài quá thì người dùng thôi đọc, tức là mất đúng thứ
    /// tính năng này đi mua.
    /// </summary>
    public const int MaxFunctionsPerScreen = 12;

    /// <summary>Trần số mục phạm vi mà MỘT màn hình được khai là đã gộp vào mình.</summary>
    public const int MaxCoversPerScreen = 8;

    private const int MaxTextChars = 200;
    private const int MaxEvidenceChars = 300;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Bảng cuối cùng cho lượt BA BÀY BẢNG: giữ các dòng khớp phạm vi đã chắt, bỏ dòng bịa/trùng, bổ sung
    /// mọi màn hình chưa được nhắc tới (trừ mục đã được gộp), và luôn xếp theo thứ tự của
    /// <paramref name="plannedScope"/>. Trả rỗng khi phạm vi trống — không có phạm vi thì bảng không có gì
    /// để hỏi.
    ///
    /// <para>
    /// Khác <see cref="Sanitize"/> ở đúng chỗ cờ <c>included</c>: ở đây mọi dòng và mọi chức năng ra TÍCH
    /// SẴN bất kể model trả gì, vì cờ đó là chỗ NGƯỜI DÙNG loại một màn hình, không phải chỗ model tự phủ
    /// nhận đề xuất của mình. Structured output buộc điền đủ trường, nên một model điền <c>false</c> cho có
    /// sẽ bỏ tích sạch bảng và người dùng gửi đi một phạm vi RỖNG trong khi tưởng mình vừa xác nhận cả
    /// ứng dụng.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> Build(IEnumerable<ScreenScopeRow>? proposed, IReadOnlyList<string> plannedScope)
        => BuildCore(proposed, plannedScope, respectIncluded: false);

    /// <summary>
    /// Bản chuẩn hoá cho dữ liệu ĐẾN TỪ TRÌNH DUYỆT. Server không tin bảng client gửi kể cả khi chính nó
    /// vừa render ra: tên màn hình vẫn phải khớp lại <paramref name="allowedScreens"/>. Khác
    /// <see cref="Build"/>: giữ đúng lựa chọn tích/bỏ tích của người dùng ở CẢ hai cấp (màn hình và chức
    /// năng), và xoá cờ khóa (bảng đã gửi thì mọi dòng là quyết định của họ).
    ///
    /// <para>
    /// <paramref name="allowedScreens"/> phải là danh sách màn hình của CHÍNH BẢNG SERVER ĐÃ RENDER, không
    /// phải <c>Project.PlannedScope</c> đọc lại lúc gửi. Hai thứ đó KHÔNG bằng nhau: lượt chắt lọc
    /// <c>PlannedScope</c> chạy ở hậu kỳ ngay lượt bày bảng (xem <c>InterviewOutlookService</c>), nên tới
    /// lúc người dùng bấm gửi thì danh sách đã bị viết lại — chỉ cần model diễn đạt khác đi một chữ là mọi
    /// dòng trượt khỏi <see cref="MatchScreen"/>, cả bảng người dùng vừa rà bị bỏ, và chỗ của nó là các mục
    /// phạm vi mới bù vào ở dạng TRẮNG. Người dùng thấy mình gửi một bảng đã điền và nhận lại một danh sách
    /// tên suông. Xem <c>ConfirmScreenScopeUseCase</c>.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> Sanitize(IEnumerable<ScreenScopeRow>? submitted, IReadOnlyList<string> allowedScreens)
    {
        var rows = BuildCore(submitted, allowedScreens, respectIncluded: true);
        foreach (var row in rows)
        {
            row.Locked = false;
            row.Evidence = string.Empty;
            foreach (var function in row.Functions)
            {
                function.Locked = false;
                function.Evidence = string.Empty;
            }
        }
        return rows;
    }

    private static List<ScreenScopeRow> BuildCore(
        IEnumerable<ScreenScopeRow>? proposed,
        IReadOnlyList<string> allowedScreens,
        bool respectIncluded)
    {
        var screens = CleanScreens(allowedScreens);
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
                Screen = screen, // chữ của DANH SÁCH CHO PHÉP, không phải chữ của model
                Purpose = Clip((row.Purpose ?? string.Empty).Trim(), MaxTextChars),
                Functions = CleanFunctions(row.Functions, respectIncluded),
                Covers = CleanCovers(row.Covers, screen),
                Included = !respectIncluded || row.Included,
                // LUẬT BẰNG CHỨNG — cờ suông không khóa được dòng nào. Xem PermissionGrant.Locked.
                Locked = evidence.Length > 0,
                Evidence = evidence
            };
        }

        var covered = CoveredScopeItems(byScreen.Values, screens);

        var result = new List<ScreenScopeRow>();
        foreach (var screen in screens)
        {
            if (byScreen.TryGetValue(screen, out var found))
                result.Add(found);
            else if (covered.Contains(Normalize(screen)))
                continue; // mục này đã nằm trong cột chức năng của một màn hình khác — xem CoveredScopeItems
            else
                // Màn hình model bỏ quên: vẫn phải có mặt, TÍCH SẴN. Đưa vào ở trạng thái bỏ tích là ra
                // quyết định loại thay người dùng, còn bỏ hẳn là làm nó biến mất khỏi mọi tầng sau.
                result.Add(new ScreenScopeRow { Screen = screen, Included = true });

            if (result.Count >= MaxRows)
                break;
        }

        return result;
    }

    /// <summary>
    /// Các mục phạm vi đã được một dòng khai là GỘP vào mình, chuẩn hoá sẵn để đối chiếu.
    ///
    /// <para>
    /// Một mục chỉ được coi là đã gộp khi KHÔNG dòng nào đứng tên nó: dòng luôn thắng lời khai gộp. Không có
    /// luật đó thì model chỉ cần khai bừa tên một màn hình thật vào <see cref="ScreenScopeRow.Covers"/> của
    /// dòng khác là màn hình ấy biến mất khỏi bảng trong im lặng — mà cả tính năng này tồn tại để không thứ
    /// gì rời khỏi phạm vi mà người dùng không nhìn thấy.
    /// </para>
    /// </summary>
    private static HashSet<string> CoveredScopeItems(IEnumerable<ScreenScopeRow> rows, IReadOnlyList<string> screens)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var owned = new HashSet<string>(rows.Select(r => Normalize(r.Screen)), StringComparer.Ordinal);

        foreach (var row in rows)
        {
            foreach (var item in row.Covers)
            {
                var match = MatchScreen(item, screens);
                if (match == null)
                    continue;

                var key = Normalize(match);
                if (!owned.Contains(key))
                    claimed.Add(key);
            }
        }

        return claimed;
    }

    /// <summary>
    /// Đọc JSON bảng màn hình đã lưu (cột DB hoặc payload client). null/rỗng/hỏng ⇒ mảng rỗng.
    ///
    /// <para>
    /// Đọc qua <see cref="JsonNode"/> chứ không deserialize thẳng vì phải nuốt được BẢN CŨ: hồi cột chức
    /// năng còn là một ô text, dòng được lưu dạng <c>"functions": "Xem, Tạo"</c> kèm <c>"flowSteps"</c> ở
    /// cấp màn hình. Deserialize thẳng thì kiểu không khớp ⇒ ném ⇒ <c>catch</c> ⇒ trả mảng rỗng, và một dự
    /// án ĐÃ chốt bảng bỗng bị coi như chưa chốt: cổng bảng phân quyền mở lại, khối "bảng đã chốt" biến khỏi
    /// ngữ cảnh, tất cả trong im lặng. Nâng cấp tại chỗ rẻ hơn nhiều so với việc đi dò xem hỏng ở đâu.
    /// </para>
    /// </summary>
    public static List<ScreenScopeRow> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<ScreenScopeRow>();

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
                return new List<ScreenScopeRow>();

            foreach (var item in array.OfType<JsonObject>())
                UpgradeLegacyRow(item);

            var rows = array.Deserialize<List<ScreenScopeRow>>(ReadOptions) ?? new List<ScreenScopeRow>();
            return rows.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Screen)).ToList();
        }
        catch
        {
            return new List<ScreenScopeRow>();
        }
    }

    /// <summary>
    /// Đưa MỘT dòng của bản cũ về hình dạng hiện tại: ô text chức năng thành các dòng con, và bước luồng ở
    /// cấp màn hình gắn xuống chức năng ĐẦU TIÊN.
    ///
    /// <para>
    /// Vì sao gắn vào chức năng đầu tiên chứ không rải đều: phép kiểm ở <see cref="UncoveredActions"/> so
    /// khớp theo cụm CHỨA-NHAU trên toàn bộ bảng, nên bước nằm ở chức năng nào cũng cho cùng kết quả "đã có
    /// người phụ trách" — thứ duy nhất phải giữ là đừng để nó rơi mất. Dòng cũ không có chức năng nào thì
    /// bước rơi thật, và nó rơi ra ĐÚNG CHỖ ồn ào nhất: dòng nhắc "chưa màn hình nào phụ trách bước…" hiện
    /// ngay dưới bảng để người dùng điền lại. Mất tiếng còn hơn mất im lặng.
    /// </para>
    /// </summary>
    private static void UpgradeLegacyRow(JsonObject row)
    {
        var functionsKey = FindKey(row, "functions");
        if (functionsKey != null && row[functionsKey] is JsonValue or JsonArray)
        {
            var upgraded = LegacyFunctions(row[functionsKey]);
            if (upgraded != null)
                row[functionsKey] = upgraded;
        }

        var stepsKey = FindKey(row, "flowSteps");
        if (stepsKey == null)
            return;

        var steps = row[stepsKey] as JsonArray;
        row.Remove(stepsKey);
        if (steps == null || steps.Count == 0)
            return;

        if (row[functionsKey ?? "functions"] is not JsonArray functions || functions.FirstOrDefault() is not JsonObject first)
            return;

        var target = first[FindKey(first, "flowSteps") ?? "flowSteps"] as JsonArray;
        if (target == null)
        {
            target = new JsonArray();
            first["flowSteps"] = target;
        }

        foreach (var step in steps.OfType<JsonValue>().ToList())
            target.Add(JsonValue.Create(step.ToString()));
    }

    /// <summary>Bản cũ của cột chức năng: một chuỗi ngăn bằng dấu phẩy, hoặc một mảng chuỗi. Đã đúng dạng ⇒ null.</summary>
    private static JsonArray? LegacyFunctions(JsonNode? node)
    {
        var names = new List<string>();
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                names.AddRange(text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                break;
            case JsonArray array when array.All(i => i is JsonValue):
                names.AddRange(array.OfType<JsonValue>().Select(i => i.ToString()));
                break;
            default:
                return null; // mảng object = đã đúng dạng hiện tại
        }

        var result = new JsonArray();
        foreach (var name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
            result.Add(new JsonObject { ["name"] = name, ["included"] = true });
        return result;
    }

    private static string? FindKey(JsonObject obj, string name)
        => obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

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
    /// đã BỎ TÍCH — hoặc đã được GỘP vào một màn hình khác — thì không bao giờ quay lại, kể cả khi nó vẫn
    /// nằm trong PlannedScope. Mở lại thứ họ vừa đóng là đúng lỗi mà bảng cột đã cấm.
    /// <c>ConfirmScreenScopeUseCase</c> ghi ngược phạm vi đã duyệt lên PlannedScope nên lượt chắt lọc kế
    /// tiếp không còn mang mục đã đóng theo nữa, nhưng phép lọc ở đây vẫn là thứ BẢO ĐẢM điều đó: lượt chắt
    /// lọc là một lời gọi LLM, và một lời gọi LLM không phải một bất biến.
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
        foreach (var item in rows.SelectMany(r => r.Covers))
            known.Add(Normalize(item));

        foreach (var raw in CleanScreens(plannedScope))
        {
            if (known.Add(Normalize(raw)))
                result.Add(raw);
        }

        return result;
    }

    /// <summary>
    /// Các BƯỚC LUỒNG đã chốt mà KHÔNG chức năng nào trong bảng nhận phụ trách — phép kiểm tất định của
    /// tính năng, chạy bằng code chứ không bằng một lời gọi LLM nữa.
    ///
    /// <para>
    /// Vì sao nó đáng có: hai danh sách này đọc riêng đều "đạt" — bảng luồng đầy đủ, bảng màn hình đầy đủ —
    /// còn chỗ hỏng nằm ở mối nối giữa chúng, đúng loại lỗi đắt nhất của cả dây chuyền. Một bước không ai
    /// phụ trách nghĩa là hoặc người dùng sẽ không có chỗ nào để làm bước đó, hoặc bước đó không có thật.
    /// Cả hai đều phải hỏi, và hỏi ngay lúc bảng còn trên màn hình rẻ hơn hẳn hỏi lại ở POC.
    /// </para>
    ///
    /// <para>
    /// Chỉ đếm chức năng CÒN TÍCH của màn hình CÒN TÍCH: bỏ tích một chức năng là bỏ luôn phần việc nó gánh,
    /// nên bước của nó phải lập tức hiện ra là chưa ai làm. Đó là phần mà bản cũ — gắn bước ở cấp màn hình —
    /// không nói được: người dùng bỏ đúng chức năng chở bước đó mà cả bảng vẫn báo "đủ".
    /// </para>
    ///
    /// <para>
    /// So khớp bằng CHỨA-NHAU sau chuẩn hoá chứ không khớp chính xác: người dùng sửa ô "phục vụ bước" bằng
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
            .SelectMany(r => r.Functions)
            .Where(f => f.Included)
            .SelectMany(f => f.FlowSteps)
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
    ///
    /// <para>
    /// Danh sách chức năng ở đây là thứ bảng phân quyền phải đứng lên: dòng của bảng đó là một cặp
    /// (màn hình × chức năng), và trước khi có khối này thì vế chức năng do model tự nghĩ ra ngay tại lượt
    /// bày bảng — tức người dùng tích quyền cho những việc chưa ai duyệt là có thật.
    /// </para>
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
        sb.AppendLine("Đây là TOÀN BỘ màn hình của ứng dụng và các chức năng trên từng màn. KHÔNG thêm màn "
            + "hình mới ngoài danh sách này, KHÔNG thêm chức năng ngoài các chức năng dưới đây, và KHÔNG hỏi "
            + "lại việc của từng màn.");

        foreach (var row in kept)
        {
            var purpose = string.IsNullOrWhiteSpace(row.Purpose) ? string.Empty : $" — {row.Purpose}";
            sb.AppendLine($"* {row.Screen}{purpose}");

            foreach (var function in row.Functions.Where(f => f.Included))
            {
                var steps = function.FlowSteps.Count > 0
                    ? $" (phục vụ bước: {string.Join("; ", function.FlowSteps)})"
                    : string.Empty;
                sb.AppendLine($"  - chức năng: {function.Name}{steps}");
            }

            var droppedFunctions = row.Functions.Where(f => !f.Included).Select(f => f.Name).ToList();
            if (droppedFunctions.Count > 0)
                sb.AppendLine($"  - chức năng người dùng đã LOẠI (đừng dựng, đừng nhắc lại): {string.Join(", ", droppedFunctions)}");

            if (row.Covers.Count > 0)
                sb.AppendLine($"  - đã gộp vào màn này: {string.Join(", ", row.Covers)}");
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
            var functions = row.Functions.Where(f => f.Included).Select(f => f.Name).ToList();
            sb.AppendLine($"- {row.Screen}{purpose}"
                + (functions.Count > 0 ? $" [chức năng: {string.Join(", ", functions)}]" : string.Empty));

            if (row.Covers.Count > 0)
                sb.AppendLine($"  (mình gộp vào màn này: {string.Join(", ", row.Covers)})");
        }

        // Màn hình và chức năng bị loại phải được NÓI RA — cùng lý do bảng cột gọi tên cả cột bị bỏ tích:
        // im lặng thì người dùng không có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại.
        var dropped = rows.Where(r => !r.Included).ToList();
        if (dropped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Các màn hình mình KHÔNG cần: " + string.Join(", ", dropped.Select(r => r.Screen)) + ".");
        }

        var droppedFunctions = rows
            .Where(r => r.Included)
            .SelectMany(r => r.Functions.Where(f => !f.Included).Select(f => $"{f.Name} (ở {r.Screen})"))
            .ToList();
        if (droppedFunctions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Các chức năng mình KHÔNG cần: " + string.Join(", ", droppedFunctions) + ".");
        }

        return sb.ToString().TrimEnd();
    }

    // ==== chuẩn hoá từng phần ====

    private static List<string> CleanScreens(IReadOnlyList<string>? screens)
    {
        var result = new List<string>();
        if (screens == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in screens)
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

    // Chức năng KHÔNG có tên thì không có gì để tích: bỏ. Đây cũng là đường mà dòng trống "thêm chức năng…"
    // của giao diện đi ra — người dùng không gõ gì vào nó thì nó không thành một chức năng.
    private static List<ScreenFunction> CleanFunctions(IEnumerable<ScreenFunction>? proposed, bool respectIncluded)
    {
        var result = new List<ScreenFunction>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var function in proposed ?? Enumerable.Empty<ScreenFunction>())
        {
            if (function == null)
                continue;

            var name = Clip((function.Name ?? string.Empty).Trim(), MaxTextChars);
            if (name.Length == 0 || !seen.Add(Normalize(name)))
                continue;

            var evidence = Clip((function.Evidence ?? string.Empty).Trim(), MaxEvidenceChars);
            result.Add(new ScreenFunction
            {
                Name = name,
                FlowSteps = CleanFlowSteps(function.FlowSteps),
                Included = !respectIncluded || function.Included,
                Locked = evidence.Length > 0,
                Evidence = evidence
            });

            if (result.Count >= MaxFunctionsPerScreen)
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
            if (result.Count >= MaxFlowStepsPerFunction)
                break;
        }
        return result;
    }

    private static List<string> CleanCovers(IEnumerable<string>? proposed, string screen)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(screen) };
        foreach (var raw in proposed ?? Enumerable.Empty<string>())
        {
            var item = (raw ?? string.Empty).Trim();
            if (item.Length == 0 || !seen.Add(Normalize(item)))
                continue;

            result.Add(Clip(item, MaxTextChars));
            if (result.Count >= MaxCoversPerScreen)
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
