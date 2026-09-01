using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

/// <summary>Một dòng call log ở dạng đã đọc ra khỏi DB, đủ để dựng bản Markdown xuất ra.</summary>
public sealed record ModelCallLogExportItem(
    Guid Id, DateTime CreatedAtUtc, string AgentName, string ModelId, string Purpose, int Step,
    string RequestJson, string ResponseText, string? ErrorMessage,
    int PromptTokens, int CachedPromptTokens, int CompletionTokens, int TotalTokens,
    long DurationMs, int? HttpStatusCode, bool IsSuccess, Guid? WorkflowRunId)
{
    public static ModelCallLogExportItem From(AgentModelCallLog log) => new(
        log.Id, log.CreatedAt, log.AgentName, log.ModelId, log.Purpose, log.Step,
        log.RequestJson, log.ResponseText, log.ErrorMessage,
        log.PromptTokens, log.CachedPromptTokens, log.CompletionTokens, log.TotalTokens,
        log.DurationMs, log.HttpStatusCode, log.IsSuccess, log.WorkflowRunId);
}

/// <summary>
/// Dựng file Markdown "mang một lời gọi model đi hỏi chỗ khác": người dùng thấy response không như ý thì
/// tải trọn ngữ cảnh của lượt gọi đó ra một file để dán cho một AI khác soi.
///
/// <para>
/// Vì sao là Markdown chứ không phải chính <c>RequestJson</c>: thứ cần đọc nằm trong <c>messages</c>, mà ở
/// dạng JSON thì mọi xuống dòng của prompt là <c>\n</c> và mọi dấu nháy bị escape — người lẫn model đều
/// phải giải mã trước khi đọc được câu đầu tiên. Ở đây mỗi message là một khối riêng có ghi rõ VAI và ĐỘ
/// DÀI, nên câu hỏi "sai vì prompt hay vì context" nhìn thấy được ngay từ mục lục.
/// </para>
///
/// <para>
/// Bản xuất KHÔNG cắt bớt nội dung. Cắt một khối context ở giữa là bỏ đi đúng thứ đang cần soi, và người
/// đọc file không có cách nào biết phần mất là phần nào. Cỡ file được chặn ở tầng trên bằng SỐ lời gọi
/// trong cụm (xem <c>ExportCallLogTurnQuery</c>), không phải bằng kéo cắt ở đây.
/// </para>
///
/// <para>
/// Ảnh gửi kèm chỉ được NÊU TÊN, giống <see cref="ModelCallRequestPreview"/>: bytes nằm trên đĩa
/// (<see cref="ModelCallImageStore"/>), nhúng base64 vào đây thì một file .md phồng lên hàng megabyte chữ
/// rác mà không ai đọc được.
/// </para>
/// </summary>
public static class ModelCallLogMarkdown
{
    /// <summary>Khối mở đầu: nói thẳng cho người/AI đọc file biết phải trả lời câu hỏi gì.</summary>
    private const string ReadingGuide =
        "> File này do ICOGenerator xuất từ màn **AI Call Logs**, chứa TRỌN VẸN thứ đã gửi cho model và thứ\n"
        + "> model trả về ở một lượt gọi.\n"
        + ">\n"
        + "> Câu hỏi cần trả lời khi đọc: response lệch là do **PROMPT** (các message `system` — chỉ dẫn,\n"
        + "> luật, khuôn output) hay do **CONTEXT** (dữ liệu được nạp vào: bản đồ bao phủ, bảng đã chốt,\n"
        + "> transcript, tài liệu nguồn)? Hai nguyên nhân đó sửa ở hai chỗ khác nhau.";

    /// <summary>Bản Markdown của MỘT lời gọi.</summary>
    public static string Render(ModelCallLogExportItem item)
    {
        var sb = new StringBuilder();
        sb.Append("# AI Call Log — ").Append(Label(item)).AppendLine();
        sb.AppendLine();
        sb.AppendLine(ReadingGuide);
        sb.AppendLine();
        AppendCall(sb, item, heading: 2, index: null);
        return sb.ToString();
    }

    /// <summary>
    /// Bản Markdown của một CỤM lời gọi (các lượt gọi cùng thuộc một lượt làm việc). Lời gọi neo — dòng
    /// người dùng bấm tải — được đánh dấu để người đọc biết bắt đầu từ đâu.
    /// </summary>
    public static string Render(IReadOnlyList<ModelCallLogExportItem> items, Guid anchorId, string groupingNote)
    {
        if (items.Count == 1)
            return Render(items[0]);

        var sb = new StringBuilder();
        sb.Append("# AI Call Log — cụm ").Append(items.Count).Append(" lời gọi").AppendLine();
        sb.AppendLine();
        sb.AppendLine(ReadingGuide);
        sb.AppendLine(">");
        sb.AppendLine("> Cụm này có NHIỀU lời gọi vì một thao tác của người dùng thường tốn vài lượt gọi model,");
        sb.AppendLine("> và output của lượt trước là input của lượt sau. Một response lệch thường bắt nguồn từ một");
        sb.AppendLine("> lời gọi KHÁC trong cùng cụm — đọc theo thứ tự thời gian, đừng đọc mỗi lời gọi được đánh dấu.");
        sb.AppendLine();
        sb.AppendLine("## Các lời gọi trong cụm");
        sb.AppendLine();
        sb.AppendLine("| # | Lời gọi | Thời điểm (UTC) | Thời lượng | Token | Kết quả |");
        sb.AppendLine("|---|---|---|---|---|---|");
        for (var i = 0; i < items.Count; i++)
        {
            var x = items[i];
            var mark = x.Id == anchorId ? " ← **đang xem**" : string.Empty;
            sb.Append("| ").Append(i + 1)
                .Append(" | ").Append(Escape(Label(x))).Append(mark)
                .Append(" | ").Append(Timestamp(x.CreatedAtUtc))
                .Append(" | ").Append(x.DurationMs).Append(" ms")
                .Append(" | ").Append(x.TotalTokens.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" | ").Append(Status(x))
                .AppendLine(" |");
        }

        sb.AppendLine();
        sb.AppendLine("> **Cụm được suy ra, không phải được lưu sẵn.** " + groupingNote);
        sb.AppendLine();

        for (var i = 0; i < items.Count; i++)
            AppendCall(sb, items[i], heading: 2, index: i + 1, isAnchor: items[i].Id == anchorId);

        return sb.ToString();
    }

    /// <summary>Tên file tải về: đọc được bằng mắt và sắp xếp được theo thời gian.</summary>
    public static string FileName(ModelCallLogExportItem item, bool cluster)
    {
        var prefix = cluster ? "call-log-cum" : "call-log";
        var purpose = Slug(item.Purpose);
        var stamp = item.CreatedAtUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return $"{prefix}-{purpose}-{stamp}.md";
    }

    private static void AppendCall(StringBuilder sb, ModelCallLogExportItem item, int heading, int? index, bool isAnchor = false)
    {
        var h = new string('#', heading);
        sb.Append(h).Append(' ');
        if (index is { } n)
            sb.Append(n).Append(". ");
        sb.Append(Label(item)).Append(" — ").Append(Timestamp(item.CreatedAtUtc));
        if (isAnchor)
            sb.Append(" ← đang xem");
        sb.AppendLine();
        sb.AppendLine();

        AppendMetadata(sb, item, heading + 1);
        AppendRequest(sb, item, heading + 1);
        AppendBlock(sb, heading + 1, "Response", item.ResponseText,
            empty: "(model không trả về nội dung nào)");

        // Khối lỗi chỉ hiện khi có lỗi thật: in một mục "Lỗi: (không có)" vào mọi bản xuất là dạy người đọc
        // lướt qua đúng cái mục quan trọng nhất khi nó có nội dung.
        if (!string.IsNullOrWhiteSpace(item.ErrorMessage))
            AppendBlock(sb, heading + 1, "Lỗi", item.ErrorMessage!, empty: string.Empty);
    }

    private static void AppendMetadata(StringBuilder sb, ModelCallLogExportItem item, int heading)
    {
        var request = Parse(item.RequestJson);

        sb.Append(new string('#', heading)).AppendLine(" Thông số");
        sb.AppendLine();
        sb.AppendLine("| Trường | Giá trị |");
        sb.AppendLine("|---|---|");
        Row(sb, "Log id", item.Id.ToString());
        Row(sb, "Agent", item.AgentName);
        Row(sb, "Model", item.ModelId);
        Row(sb, "Purpose", item.Purpose);
        if (item.Step > 1)
            Row(sb, "Step", item.Step.ToString(CultureInfo.InvariantCulture));
        Row(sb, "Thời điểm (UTC)", Timestamp(item.CreatedAtUtc));
        Row(sb, "Kết quả", Status(item));
        Row(sb, "Thời lượng", item.DurationMs + " ms");
        Row(sb, "Token", Tokens(item));
        Row(sb, "Workflow run", item.WorkflowRunId?.ToString() ?? "(không thuộc run nào — lời gọi tương tác)");

        // Các tham số THẬT SỰ đã đi ra, đọc lại từ chính RequestJson: response_format và temperature là hai
        // thứ endpoint hay từ chối, còn stream quyết định lời gọi có đi đường SSE hay không.
        foreach (var (label, key) in ParameterKeys)
        {
            if (request?[key] is { } value)
                Row(sb, label, Inline(value));
        }

        if (request?["_approxBodyBytes"]?.GetValue<long>() is { } bytes)
            Row(sb, "Cỡ gói tin (ước lượng)", Bytes(bytes));

        sb.AppendLine();
    }

    private static readonly (string Label, string Key)[] ParameterKeys =
    {
        ("temperature", "temperature"),
        ("max_tokens", "max_tokens"),
        ("stream", "stream"),
        ("response_format", "response_format"),
        ("tools", "tools"),
        ("thinking", "thinking"),
        ("prompt_cache_key", "prompt_cache_key"),
        ("prompt_cache_retention", "prompt_cache_retention"),
    };

    private static void AppendRequest(StringBuilder sb, ModelCallLogExportItem item, int heading)
    {
        var request = Parse(item.RequestJson);
        var messages = request?["messages"] as JsonArray;

        // RequestJson hỏng/khuyết (log rất cũ, hoặc lời gọi chết trước khi dựng được preview): đổ nguyên
        // chuỗi ra chứ KHÔNG bỏ mục — bản xuất mà thiếu hẳn phần request thì vô dụng đúng lúc cần nhất.
        if (messages == null)
        {
            AppendBlock(sb, heading, "Request (không đọc được thành messages — đổ nguyên văn)",
                item.RequestJson, empty: "(trống)");
            return;
        }

        sb.Append(new string('#', heading)).Append(" Request — ").Append(messages.Count).AppendLine(" message");
        sb.AppendLine();

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var role = message?["role"]?.GetValue<string>() ?? "?";
            var content = message?["content"];

            if (content is JsonValue value && value.TryGetValue<string>(out var text))
            {
                AppendMessage(sb, heading + 1, i + 1, role, text.Length + " ký tự");
                AppendFenced(sb, text);
                continue;
            }

            if (content is JsonArray parts)
            {
                AppendMessage(sb, heading + 1, i + 1, role, parts.Count + " phần");
                foreach (var part in parts)
                    AppendPart(sb, part);
                continue;
            }

            AppendMessage(sb, heading + 1, i + 1, role, "nội dung rỗng");
        }
    }

    private static void AppendMessage(StringBuilder sb, int heading, int index, string role, string note)
    {
        sb.Append(new string('#', heading)).Append(" [").Append(index).Append("] ").Append(role)
            .Append(" — ").Append(note).AppendLine();
        sb.AppendLine();
    }

    /// <summary>
    /// Một phần nội dung của message nhiều part. Part chữ đi ra dạng khối code như message thường; các part
    /// còn lại (ảnh/file/uri) chỉ có phần MÔ TẢ trong log nên ở đây cũng chỉ là một dòng liệt kê — kèm câu
    /// nói rõ bytes không nằm trong file, để người đọc không đi tìm một thứ chưa từng được xuất ra.
    /// </summary>
    private static void AppendPart(StringBuilder sb, JsonNode? part)
    {
        var type = part?["type"]?.GetValue<string>();
        if (type == "text")
        {
            AppendFenced(sb, part?["text"]?.GetValue<string>() ?? string.Empty);
            return;
        }

        var name = part?["name"]?.GetValue<string>();
        var mediaType = part?["mediaType"]?.GetValue<string>();
        var bytes = part?["bytes"]?.GetValue<long>();
        var index = part?["index"]?.GetValue<int>();

        sb.Append("- **").Append(type ?? "?").Append("**");
        if (index is { } n) sb.Append(" #").Append(n);
        if (!string.IsNullOrWhiteSpace(name)) sb.Append(" · ").Append(Escape(name!));
        if (!string.IsNullOrWhiteSpace(mediaType)) sb.Append(" · ").Append(mediaType);
        if (bytes is { } size) sb.Append(" · ").Append(Bytes(size));
        sb.AppendLine(" — *bytes không nằm trong file này; xem ảnh ở màn Model Invocation Detail.*");
        sb.AppendLine();
    }

    private static void AppendBlock(StringBuilder sb, int heading, string title, string body, string empty)
    {
        sb.Append(new string('#', heading)).Append(' ').AppendLine(title);
        sb.AppendLine();
        if (string.IsNullOrEmpty(body))
        {
            if (empty.Length > 0)
            {
                sb.AppendLine(empty);
                sb.AppendLine();
            }
            return;
        }
        AppendFenced(sb, body);
    }

    /// <summary>
    /// Khối code bao quanh nội dung. Rào code dài hơn chuỗi backtick DÀI NHẤT bên trong: prompt của repo này
    /// là file Markdown có sẵn khối ```json bên trong, rào ba backtick cứng sẽ bị chính nội dung đóng sớm và
    /// nửa sau của prompt tràn ra ngoài dưới dạng Markdown đã render — đúng chỗ người đọc cần đọc nguyên văn.
    /// </summary>
    private static void AppendFenced(StringBuilder sb, string content)
    {
        var fence = new string('`', FenceLength(content));
        sb.Append(fence).AppendLine("text");
        sb.AppendLine(content.TrimEnd('\n', '\r'));
        sb.AppendLine(fence);
        sb.AppendLine();
    }

    internal static int FenceLength(string content)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in content)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest) longest = run;
        }
        return Math.Max(3, longest + 1);
    }

    private static JsonObject? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Row(StringBuilder sb, string label, string value) =>
        sb.Append("| ").Append(label).Append(" | ").Append(Escape(value)).AppendLine(" |");

    private static string Label(ModelCallLogExportItem item) =>
        string.IsNullOrWhiteSpace(item.Purpose) ? item.AgentName : item.Purpose;

    private static string Status(ModelCallLogExportItem item)
    {
        var status = item.IsSuccess ? "Success" : "Error";
        return item.HttpStatusCode is { } code ? $"{status} (HTTP {code})" : status;
    }

    private static string Tokens(ModelCallLogExportItem item)
    {
        var sb = new StringBuilder();
        sb.Append("tổng ").Append(item.TotalTokens.ToString("N0", CultureInfo.InvariantCulture));
        sb.Append(" · prompt ").Append(item.PromptTokens.ToString("N0", CultureInfo.InvariantCulture));
        // Phần cache nằm TRONG prompt chứ không cộng thêm (xem AgentModelCallLog.CachedPromptTokens) — nói
        // rõ ở đây vì người đọc bản xuất không có chú thích của cột DB bên cạnh.
        if (item.CachedPromptTokens > 0)
            sb.Append(" (trong đó ").Append(item.CachedPromptTokens.ToString("N0", CultureInfo.InvariantCulture)).Append(" đọc từ cache)");
        sb.Append(" · completion ").Append(item.CompletionTokens.ToString("N0", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string Timestamp(DateTime utc) =>
        utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    private static string Bytes(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024d).ToString("N1", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024d * 1024d)).ToString("N1", CultureInfo.InvariantCulture) + " MB",
    };

    private static string Inline(JsonNode value) =>
        value.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    /// <summary>Giá trị đi vào một Ô BẢNG Markdown: '|' không escape sẽ cắt hàng thành nhiều cột.</summary>
    private static string Escape(string value) =>
        value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    /// <summary>Phần tên file lấy từ dữ liệu: chỉ giữ chữ/số/gạch để không đụng luật đặt tên của OS nào.</summary>
    private static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (c is '-' or '_' or ' ') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "call" : slug;
    }
}
