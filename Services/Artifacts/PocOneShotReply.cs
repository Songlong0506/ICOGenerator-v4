using System.Text.Json;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Phần "shell" của bản demo mà model khai báo: tên ứng dụng, breadcrumb, menu, danh sách vai — đúng bốn
/// tham số ngoài <c>content</c> của <c>WorkspaceTools.SetPocContent</c>. Giữ <see cref="JsonElement"/> thô cho
/// <see cref="NavItems"/>/<see cref="Roles"/> để đi qua CÙNG parser (<see cref="PocNavItem.ParseList"/>,
/// <see cref="PocRole.ParseList"/>) với đường agent, không có bản đọc thứ hai.
/// </summary>
public sealed record PocShellMeta(string? AppName, string? Breadcrumb, JsonElement? NavItems, JsonElement? Roles);

/// <summary>Bản demo sinh trong MỘT lượt trả lời — xem <see cref="PocOneShotReply.TryParseBuild"/>.</summary>
public sealed record PocBuildDraft(PocShellMeta Meta, string Content, string Script, string Handoff);

/// <summary>Một chỗ sửa "tìm đúng đoạn này, thay bằng đoạn kia" trong poc-demo.html.</summary>
public sealed record PocTextEdit(string Find, string Replace);

/// <summary>
/// Bản vá một vòng sửa theo báo cáo audit — xem <see cref="PocOneShotReply.TryParseRepair"/>. Mọi phần đều
/// tùy chọn; <see cref="IsEmpty"/> nghĩa là model không đề xuất gì để sửa.
/// </summary>
public sealed record PocRepairPatch(
    PocShellMeta? Meta,
    IReadOnlyList<PocTextEdit> Edits,
    string? AppendContent,
    string? Script,
    string Notes)
{
    public bool IsEmpty =>
        Meta == null && Edits.Count == 0 && string.IsNullOrWhiteSpace(AppendContent) && string.IsNullOrWhiteSpace(Script);
}

/// <summary>
/// Đọc câu trả lời của hai lời gọi dựng POC không qua agent (<c>PocOneShotBuilder</c>): lượt SINH cả bản
/// demo và lượt SỬA theo báo cáo audit.
///
/// <para>
/// Vì sao là các dòng đánh dấu <c>===POC_XXX===</c> chứ không phải JSON: phần lớn câu trả lời là HTML và
/// JavaScript — đầy dấu nháy kép, xuống dòng và gạch chéo ngược. Nhét chúng vào chuỗi JSON thì mỗi ký tự
/// đó thành hai ký tự (tốn thêm token đúng ở chỗ đắt nhất của lượt sinh), và chỉ cần model quên escape
/// một dấu nháy là cả bản demo không parse được. Dòng đánh dấu thì không cần escape gì; phần duy nhất là
/// JSON (<c>POC_META</c>) đã sẵn là JSON nhỏ.
/// </para>
///
/// <para>
/// <c>===POC_END===</c> là bằng chứng câu trả lời không bị cắt giữa chừng (chạm trần token output): thiếu
/// nó thì bản demo đọc được tới đâu cũng KHÔNG dùng — một bản demo cụt (script dừng giữa hàm) qua được
/// parser nhưng chết ngay khi mở. Caller rơi về đường agent nhiều bước, vốn sinh ra để chịu được trần đó.
/// </para>
/// </summary>
public static class PocOneShotReply
{
    public const string Meta = "POC_META";
    public const string Content = "POC_CONTENT";
    public const string Script = "POC_SCRIPT";
    public const string Handoff = "POC_HANDOFF";
    public const string Find = "POC_FIND";
    public const string Replace = "POC_REPLACE";
    public const string AppendContent = "POC_APPEND_CONTENT";
    public const string Notes = "POC_NOTES";
    public const string End = "POC_END";

    public static string Marker(string name) => $"==={name}===";

    // Dòng đánh dấu phải đứng MỘT MÌNH trên dòng: chuỗi "===POC_END===" nằm trong một literal JS hay một
    // câu giải thích không được cắt ngang bản demo.
    private static readonly Regex MarkerLine = new(@"^[ \t]*===(POC_[A-Z_]+)===[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Đọc lượt SINH: bắt buộc có <c>POC_META</c> (JSON đọc được), <c>POC_CONTENT</c> không rỗng và dòng
    /// <c>POC_END</c>; <c>POC_SCRIPT</c>/<c>POC_HANDOFF</c> tùy chọn. Trả <c>null</c> kèm lý do khi thiếu.
    /// </summary>
    public static PocBuildDraft? TryParseBuild(string? reply, out string error)
    {
        var sections = Split(reply, out var ended);
        if (!ended)
        {
            error = sections.Count == 0
                ? "câu trả lời không có dòng đánh dấu ===POC_…=== nào"
                : "câu trả lời thiếu dòng ===POC_END=== — nhiều khả năng bị cắt do chạm trần token output";
            return null;
        }

        var metaText = First(sections, Meta);
        if (metaText == null || !TryParseMeta(metaText, out var meta))
        {
            error = "thiếu khối ===POC_META=== hoặc JSON trong đó không đọc được";
            return null;
        }

        var content = Unfence(First(sections, Content) ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            error = "khối ===POC_CONTENT=== rỗng";
            return null;
        }

        error = string.Empty;
        return new PocBuildDraft(
            meta!,
            content,
            Unfence(First(sections, Script) ?? string.Empty).Trim(),
            (First(sections, Handoff) ?? string.Empty).Trim());
    }

    /// <summary>
    /// Đọc lượt SỬA: các cặp <c>POC_FIND</c>/<c>POC_REPLACE</c> liền nhau, cùng các khối tùy chọn
    /// <c>POC_META</c>, <c>POC_APPEND_CONTENT</c>, <c>POC_SCRIPT</c> (thay CẢ vùng script), <c>POC_NOTES</c>.
    /// Bắt buộc có <c>POC_END</c> — một bản vá cụt (thay script bằng nửa script) còn tệ hơn không vá.
    /// </summary>
    public static PocRepairPatch? TryParseRepair(string? reply, out string error)
    {
        var sections = Split(reply, out var ended);
        if (!ended)
        {
            error = sections.Count == 0
                ? "câu trả lời không có dòng đánh dấu ===POC_…=== nào"
                : "câu trả lời thiếu dòng ===POC_END=== — nhiều khả năng bị cắt do chạm trần token output";
            return null;
        }

        PocShellMeta? meta = null;
        var metaText = First(sections, Meta);
        if (metaText != null && !TryParseMeta(metaText, out meta))
        {
            error = "JSON trong khối ===POC_META=== không đọc được";
            return null;
        }

        var edits = new List<PocTextEdit>();
        for (var i = 0; i < sections.Count; i++)
        {
            if (sections[i].Name != Find)
                continue;
            if (i + 1 >= sections.Count || sections[i + 1].Name != Replace)
            {
                error = "một khối ===POC_FIND=== không có ===POC_REPLACE=== đi liền sau";
                return null;
            }

            var find = Unfence(sections[i].Body);
            if (find.Trim().Length > 0)
                edits.Add(new PocTextEdit(find, Unfence(sections[i + 1].Body)));
            i++;
        }

        var append = First(sections, AppendContent);
        var script = First(sections, Script);
        error = string.Empty;
        return new PocRepairPatch(
            meta,
            edits,
            append == null ? null : Unfence(append).Trim(),
            script == null ? null : Unfence(script).Trim(),
            (First(sections, Notes) ?? string.Empty).Trim());
    }

    /// <summary>
    /// Áp lần lượt các chỗ sửa lên các VÙNG của bản demo (<paramref name="regions"/> — vùng nội dung và vùng
    /// script, đúng hai thứ model được xem). Một chỗ sửa chỉ được áp khi đoạn cần tìm khớp ĐÚNG MỘT chỗ trên
    /// tất cả các vùng — cùng luật với tool <c>ReplaceInFile</c>, vì thay nhầm chỗ thứ hai là hỏng một màn
    /// hình khác mà không ai biết. Không tìm trong phần shell: model không được xem nó, nên một đoạn khớp
    /// ở đó chỉ có thể là khớp nhầm. Khớp nguyên văn trước; không thấy thì thử bản đã bỏ khoảng trắng
    /// đầu/cuối (model hay lệch một dòng trống quanh khối). Chỗ không áp được không làm hỏng các chỗ khác:
    /// nó được trả về để vòng sửa sau biết.
    /// </summary>
    public static (string[] Regions, IReadOnlyList<string> Failures) ApplyEdits(IReadOnlyList<string> regions, IEnumerable<PocTextEdit> edits)
    {
        var result = regions.ToArray();
        var failures = new List<string>();
        foreach (var edit in edits)
        {
            var (find, replace) = (edit.Find, edit.Replace);
            var count = CountAll(result, find);
            if (count == 0)
            {
                var trimmed = find.Trim();
                var trimmedCount = CountAll(result, trimmed);
                if (trimmedCount > 0)
                    (find, replace, count) = (trimmed, replace.Trim(), trimmedCount);
            }

            if (count == 1)
            {
                var region = Array.FindIndex(result, r => Count(r, find) == 1);
                var index = result[region].IndexOf(find, StringComparison.Ordinal);
                result[region] = result[region][..index] + replace + result[region][(index + find.Length)..];
                continue;
            }

            failures.Add(count == 0
                ? $"Không tìm thấy đoạn cần sửa: «{Preview(edit.Find)}»"
                : $"Đoạn cần sửa khớp {count} chỗ (phải khớp đúng một chỗ — thêm ngữ cảnh xung quanh): «{Preview(edit.Find)}»");
        }

        return (result, failures);
    }

    private static int CountAll(IEnumerable<string> regions, string needle) => regions.Sum(r => Count(r, needle));

    // ---- nội bộ ----

    private sealed record Section(string Name, string Body);

    private static List<Section> Split(string? reply, out bool ended)
    {
        ended = false;
        var sections = new List<Section>();
        if (string.IsNullOrEmpty(reply))
            return sections;

        var matches = MarkerLine.Matches(reply);
        for (var i = 0; i < matches.Count; i++)
        {
            var name = matches[i].Groups[1].Value;
            if (name == End)
            {
                ended = true;
                break;
            }

            var bodyStart = matches[i].Index + matches[i].Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : reply.Length;
            sections.Add(new Section(name, TrimMarkerNewlines(reply[bodyStart..bodyEnd])));
        }

        return sections;
    }

    // Bỏ đúng MỘT xuống dòng ngay sau dòng đánh dấu và MỘT xuống dòng ngay trước dòng đánh dấu kế — phần
    // còn lại giữ nguyên văn, vì thân của POC_FIND phải khớp từng ký tự với file.
    private static string TrimMarkerNewlines(string body)
    {
        if (body.StartsWith("\r\n", StringComparison.Ordinal)) body = body[2..];
        else if (body.StartsWith('\n')) body = body[1..];

        if (body.EndsWith("\r\n", StringComparison.Ordinal)) body = body[..^2];
        else if (body.EndsWith('\n')) body = body[..^1];
        return body;
    }

    private static string? First(List<Section> sections, string name) =>
        sections.FirstOrDefault(s => s.Name == name)?.Body;

    // Model hay bọc khối code trong hàng rào ```html … ``` dù không được yêu cầu — bóc nếu CẢ khối là một
    // hàng rào (không đụng tới ``` nằm giữa nội dung, vd trong một chuỗi JS).
    private static string Unfence(string body)
    {
        var trimmed = body.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal) || trimmed.Length < 6)
            return body;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return body;
        var inner = trimmed[(firstNewline + 1)..^3];
        return inner.EndsWith('\n') ? inner[..^1].TrimEnd('\r') : inner;
    }

    private static bool TryParseMeta(string text, out PocShellMeta? meta)
    {
        meta = null;
        var json = Unfence(text).Trim();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var root = doc.RootElement;
            meta = new PocShellMeta(
                ReadString(root, "appName"),
                ReadString(root, "breadcrumb"),
                ReadElement(root, "navItems"),
                ReadElement(root, "roles"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Clone: phần tử phải sống lâu hơn JsonDocument đang bị dispose.
    private static JsonElement? ReadElement(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.Clone()
            : null;

    private static int Count(string haystack, string needle)
    {
        if (needle.Length == 0)
            return 0;
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string Preview(string text)
    {
        var oneLine = Regex.Replace(text.Trim(), @"\s+", " ");
        return oneLine.Length <= 160 ? oneLine : oneLine[..160] + "…";
    }
}
