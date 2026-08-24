using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Kết xuất MỘT lượt hội thoại thành text cho các ngữ cảnh gửi LLM (transcript soạn Product Brief,
/// distill bản đồ bao phủ). Điểm mấu chốt & lý do tồn tại: đính kèm các đáp án gợi ý
/// (<see cref="AgentConversation.Suggestions"/>) của lượt BA ngay sau câu hỏi. Không có chúng, một câu trả
/// lời THAM CHIẾU như "Cả hai mục tiêu trên" / "Tất cả các mục trên" trỏ tới những lựa chọn mà reader chưa
/// từng thấy → mất ngữ cảnh (bản đồ bao phủ không ghi được thông tin, Product Brief hiểu sai).
/// <para>
/// Gom về MỘT chỗ để mọi nơi đọc hội thoại đều nhất quán: trước đây <see cref="ConversationTranscriptBuilder"/>
/// và <see cref="RequirementCoverageService"/> mỗi nơi tự dựng text và cùng bỏ sót suggestions; tách riêng
/// khiến reader thứ N dễ tái lập lại đúng lỗi này. Suggestions chỉ gắn với lượt BA (lượt user không có).
/// </para>
/// </summary>
public static class ConversationTurnRenderer
{
    public static bool IsAssistant(AgentConversation turn) => turn.Role == "assistant";

    /// <summary>
    /// Nhãn vai + nội dung lượt, và với lượt BA có gợi ý thì kèm danh sách lựa chọn đã đưa ra (đánh số để
    /// câu trả lời tham chiếu "Cả hai"/"Tất cả" nối được về đúng option). KHÔNG kèm bullet/prefix của caller.
    /// </summary>
    public static string Render(AgentConversation turn)
    {
        var isAssistant = IsAssistant(turn);
        var label = isAssistant ? "BA" : "Người dùng";
        var message = (turn.Message ?? string.Empty).Trim();

        if (!isAssistant)
            return $"{label}: {message}";

        var rendered = $"{label}: {message}";

        var suggestions = ParseSuggestions(turn.Suggestions);
        if (suggestions.Count > 0)
        {
            var options = string.Join("; ", suggestions.Select((s, i) => $"[{i + 1}] {s}"));
            rendered += $"\n   (Các lựa chọn gợi ý đã đưa cho người dùng: {options})";
        }

        // Lượt hỏi GỘP: Message chỉ là câu dẫn ngắn, các câu hỏi thật nằm ở cột riêng. Không render thì
        // mọi reader transcript (bản đồ bao phủ, Product Brief, decision log) chỉ thấy câu trả lời mà
        // không biết nó trả lời cho câu hỏi nào — đúng kiểu mất ngữ cảnh mà lớp này sinh ra để chặn.
        var questions = ParseQuestions(turn.Questions);
        if (questions.Count > 0)
        {
            var lines = questions.Select((q, i) =>
            {
                var group = q.Group.Trim();
                var head = $"[{i + 1}] {(group.Length > 0 ? $"{group} — " : "")}{q.Question.Trim()}";
                return q.Suggestions.Count > 0
                    ? $"{head} (gợi ý: {string.Join(" / ", q.Suggestions)})"
                    : head;
            });
            rendered += $"\n   (Các câu hỏi đã đặt trong lượt này: {string.Join("; ", lines)})";
        }

        // BẢNG CỘT BA đã đưa ra ở lượt đọc bảng tính nằm ở cột riêng (ColumnMap). Render GỌN — chỉ tên các
        // cột đã bày ra và các cột BA đề xuất là "có dùng" — vì bản đầy đủ (kèm nghĩa từng cột) sẽ quay lại
        // transcript ngay ở lượt sau dưới dạng câu trả lời của người dùng; in cả hai là chép đôi một bảng
        // 18 dòng vào mọi ngữ cảnh đọc hội thoại. Cái mà reader thật sự cần ở đây là biết một câu "Đúng rồi"
        // của người dùng đang gật với danh sách nào.
        var columnMap = ParseColumnMap(turn.ColumnMap);
        if (columnMap.Count > 0)
        {
            var proposed = columnMap.Where(c => c.Used).Select(c => c.Column).ToList();
            var offered = string.Join(", ", columnMap.Select(c => c.Column));
            rendered += $"\n   (Bảng cột đã đưa cho người dùng tích: {offered}"
                + (proposed.Count > 0 ? $"; BA đề xuất DÙNG: {string.Join(", ", proposed)})" : ")");
        }

        // BẢNG PHÂN QUYỀN BA đã bày ra ở lượt chốt quyền nằm ở cột riêng (PermissionMatrix). Render GỌN —
        // chỉ các màn hình/chức năng đã bày ra — vì bản đầy đủ quay lại transcript ngay ở lượt sau dưới
        // dạng tin nhắn của người dùng (PermissionMatrixBuilder.RenderUserMessage). Cái reader cần ở đây
        // chỉ là biết lượt trả lời kế tiếp đang trả lời cho bảng nào.
        var permissionMatrix = ParsePermissionMatrix(turn.PermissionMatrix);
        if (permissionMatrix.Count > 0)
        {
            var screens = permissionMatrix
                .GroupBy(r => r.Screen, StringComparer.Ordinal)
                .Select(g => $"{g.Key} ({string.Join("/", g.Select(r => r.Function))})");
            rendered += $"\n   (Bảng phân quyền đã đưa cho người dùng chọn: {string.Join("; ", screens)})";
        }

        // Sơ đồ luồng ở cột riêng (FlowDiagram) — DỮ LIỆU CŨ: lượt mới không còn vẽ sơ đồ nào (xem
        // BAConversationLog), nhưng các dự án đã chạy trước đó có sơ đồ đã thật sự trình bày cho người
        // dùng, và không render thì các reader transcript (bản đồ bao phủ, bước soạn Product Brief) mất
        // hẳn chuỗi bước ấy. Dự án mới lấy luồng từ khối "bảng đã chốt" của bảng luồng.
        var flowSteps = ParseFlowDiagram(turn.FlowDiagram);
        if (flowSteps.Count > 0)
        {
            var steps = flowSteps.Select((s, i) =>
            {
                var actor = s.Actor.Trim();
                var outcome = s.Outcome.Trim();
                var step = $"{i + 1}. {(actor.Length > 0 ? $"{actor}: " : "")}{s.Action.Trim()}";
                return outcome.Length > 0 ? $"{step} → {outcome}" : step;
            });
            rendered += $"\n   (Sơ đồ luồng nghiệp vụ đã trình bày cho người dùng xác nhận: {string.Join("; ", steps)})";
        }

        return rendered;
    }

    /// <summary>
    /// Giải mã cột <see cref="AgentConversation.FlowDiagram"/> (JSON array <see cref="FlowStep"/>) an
    /// toàn như <see cref="ParseSuggestions"/>: null/rỗng/hỏng đều trả mảng rỗng. Bước không có hành
    /// động bị bỏ (không có gì để kể).
    /// </summary>
    public static List<FlowStep> ParseFlowDiagram(string? flowDiagramJson)
    {
        if (string.IsNullOrWhiteSpace(flowDiagramJson))
            return new List<FlowStep>();

        try
        {
            var steps = JsonSerializer.Deserialize<List<FlowStep>>(flowDiagramJson) ?? new List<FlowStep>();
            return steps.Where(s => !string.IsNullOrWhiteSpace(s.Action)).ToList();
        }
        catch
        {
            // Dữ liệu cũ/không hợp lệ: bỏ qua, coi như không có sơ đồ.
            return new List<FlowStep>();
        }
    }

    /// <summary>
    /// Giải mã cột <see cref="AgentConversation.Questions"/> (JSON array <see cref="BAChatQuestion"/>)
    /// an toàn như <see cref="ParseSuggestions"/>: null/rỗng/hỏng đều trả mảng rỗng. Câu hỏi rỗng bị bỏ
    /// (không có gì để hỏi).
    /// </summary>
    public static List<BAChatQuestion> ParseQuestions(string? questionsJson)
    {
        if (string.IsNullOrWhiteSpace(questionsJson))
            return new List<BAChatQuestion>();

        try
        {
            var questions = JsonSerializer.Deserialize<List<BAChatQuestion>>(questionsJson) ?? new List<BAChatQuestion>();
            return questions.Where(q => !string.IsNullOrWhiteSpace(q.Question)).ToList();
        }
        catch
        {
            // Dữ liệu cũ/không hợp lệ: bỏ qua, coi như lượt hỏi thường.
            return new List<BAChatQuestion>();
        }
    }

    /// <summary>
    /// Giải mã cột <see cref="AgentConversation.ColumnMap"/> (JSON array <see cref="SourceColumnNote"/>)
    /// an toàn như <see cref="ParseSuggestions"/>. Dùng chung cho đường render transcript và đường render
    /// lại bảng cột sau khi tải lại trang (Views/Requirements/Index.cshtml).
    /// </summary>
    public static List<SourceColumnNote> ParseColumnMap(string? columnMapJson)
        => SourceColumnMapBuilder.Parse(columnMapJson);

    /// <summary>
    /// Giải mã cột <see cref="AgentConversation.PermissionMatrix"/> (JSON array
    /// <see cref="PermissionMatrixRow"/>) an toàn như <see cref="ParseColumnMap"/>. Dùng chung cho đường
    /// render transcript và đường render lại bảng sau khi tải lại trang (Views/Requirements/Index.cshtml).
    /// </summary>
    public static List<PermissionMatrixRow> ParsePermissionMatrix(string? permissionMatrixJson)
        => PermissionMatrixBuilder.Parse(permissionMatrixJson);

    /// <summary>Giải mã cột <see cref="AgentConversation.FlowMap"/> — như <see cref="ParsePermissionMatrix"/>.</summary>
    public static List<FlowMapRow> ParseFlowMap(string? flowMapJson)
        => FlowMapBuilder.Parse(flowMapJson);

    /// <summary>Giải mã cột <see cref="AgentConversation.ScreenScopeMap"/> — như <see cref="ParsePermissionMatrix"/>.</summary>
    public static List<ScreenScopeRow> ParseScreenScopeMap(string? screenScopeJson)
        => ScreenScopeMapBuilder.Parse(screenScopeJson);

    /// <summary>Giải mã cột <see cref="AgentConversation.EntityMap"/> — như <see cref="ParsePermissionMatrix"/>.</summary>
    public static List<EntityMapRow> ParseEntityMap(string? entityMapJson)
        => EntityMapBuilder.Parse(entityMapJson);

    /// <summary>Giải mã cột <see cref="AgentConversation.ReportMap"/> — như <see cref="ParsePermissionMatrix"/>.</summary>
    public static List<ReportMapRow> ParseReportMap(string? reportMapJson)
        => ReportMapBuilder.Parse(reportMapJson);

    /// <summary>Giải mã cột <see cref="AgentConversation.NotificationMap"/> — như <see cref="ParsePermissionMatrix"/>.</summary>
    public static List<NotificationMapRow> ParseNotificationMap(string? notificationMapJson)
        => NotificationMapBuilder.Parse(notificationMapJson);

    /// <summary>
    /// Giải mã cột <see cref="AgentConversation.Attachments"/> (JSON array <see cref="ChatAttachment"/>)
    /// an toàn như <see cref="ParseSuggestions"/>. KHÔNG dùng khi render transcript gửi LLM (tên file đã
    /// nằm trong khối ngữ cảnh nguồn), nhưng cần cho bản xuất hội thoại: người đọc bản xuất phải biết lượt
    /// nào là lượt người dùng đính kèm file, nếu không thì lượt BA đọc file ngay sau đó trông như BA tự
    /// nhiên biết nội dung một tài liệu chưa ai gửi.
    /// </summary>
    public static List<ChatAttachment> ParseAttachments(string? attachmentsJson)
    {
        if (string.IsNullOrWhiteSpace(attachmentsJson))
            return new List<ChatAttachment>();

        try
        {
            return JsonSerializer.Deserialize<List<ChatAttachment>>(
                       attachmentsJson,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new List<ChatAttachment>();
        }
        catch
        {
            // Dữ liệu cũ/không hợp lệ: bỏ qua, coi như lượt không đính kèm.
            return new List<ChatAttachment>();
        }
    }

    /// <summary>
    /// Giải mã cột <see cref="AgentConversation.Suggestions"/> (JSON array chuỗi) an toàn: null/rỗng/hỏng
    /// đều trả mảng rỗng. Dùng chung cho cả đường render transcript lẫn <c>BuildAssistantContext</c> (dựng
    /// lại lượt BA cũ đúng JSON để củng cố format) trong <see cref="BAChatService"/>.
    /// </summary>
    public static List<string> ParseSuggestions(string? suggestionsJson)
    {
        if (string.IsNullOrWhiteSpace(suggestionsJson))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(suggestionsJson) ?? new List<string>();
        }
        catch
        {
            // Dữ liệu cũ/không hợp lệ: bỏ qua, coi như không có gợi ý.
            return new List<string>();
        }
    }
}
