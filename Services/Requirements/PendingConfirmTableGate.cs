using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Một BẢNG CHỐT đang treo trên màn hình, chờ người dùng bấm gửi.
/// </summary>
/// <param name="Name">Tên bảng đúng như người dùng nhìn thấy ("bảng báo cáo").</param>
/// <param name="SendLabel">Nhãn đúng của nút gửi bảng đó ("Gửi bảng báo cáo").</param>
public sealed record PendingConfirmTable(string Name, string SendLabel)
{
    /// <summary>
    /// Dòng chữ ở cổng tạo tài liệu khi cổng ĐÓNG vì bảng này. Nói ba điều người dùng cần: vì sao chưa có
    /// nút, phải làm gì, và chuyện gì xảy ra sau đó. Bản JS dựng lại đúng câu này (requirements.js,
    /// tableGateHint) — sửa một bên thì sửa cả hai.
    /// </summary>
    public string GateHint =>
        $"Mình chưa mở nút tạo tài liệu vì {Name} ngay phía trên còn đang chờ anh/chị chốt. "
        + $"Anh/chị rà lại rồi bấm \"{SendLabel}\" giúp mình — gửi xong mình mời tạo bản mô tả ngay.";

    /// <summary>
    /// Lượt BA thay cho vòng soạn tài liệu bị chặn (đường bấm nút từ một trang đã cũ, đường POC-feedback,
    /// đường ghi chú trên bản xem trước). Phải TỰ ĐỨNG ĐƯỢC vì nó là thứ duy nhất người dùng nhìn thấy
    /// khi run kết thúc ở trạng thái "cần bổ sung".
    /// </summary>
    public string BlockedTurn =>
        $"Mình chưa soạn bản mô tả được vì {Name} vẫn đang chờ anh/chị chốt — nội dung của bảng đó là một "
        + $"phần của tài liệu, soạn trước rồi chốt sau là tài liệu vừa ra đã cũ. Anh/chị rà lại {Name} "
        + $"trong khung chat rồi bấm \"{SendLabel}\" giúp mình nhé, xong mình soạn ngay.";
}

/// <summary>
/// Cổng TẤT ĐỊNH trả lời đúng một câu hỏi: "trên màn hình còn bảng chốt nào đang chờ người dùng gửi
/// không?" — và nếu có thì bảng nào.
///
/// <para>
/// <b>Vì sao cổng này phải tồn tại.</b> Cổng tạo tài liệu (<c>#writeReqZone</c>) mở ở
/// <c>ready</c> theo HAI đường: lượt BA mới nhất mời bấm "Write Requirement", HOẶC bản draft đã có và cổng
/// readiness đang đủ (đường lùi cho bản Brief đã cũ — xem <c>Views/Requirements/Index.cshtml</c>). Đường
/// thứ hai KHÔNG đọc lượt cuối, nên nó mở cổng cả ở lượt mà BA vừa bày một bảng ra và vừa nói "rà lại rồi
/// bấm Gửi bảng ... giúp mình". Ca thật (dự án JD Libary): Brief đã có, người dùng nhắn thêm hai báo cáo,
/// <see cref="ReportMapGate"/> mở và BA bày BẢNG BÁO CÁO — nhưng nút tạo tài liệu cũng sáng, người dùng bấm
/// nút thay vì gửi bảng, và vòng soạn chạy trên một hội thoại mà bảng báo cáo còn chưa chốt. Cái giá:
/// <c>Project.ReportMap</c> vẫn null nên <c>ConfirmReportMapUseCase</c> chưa gieo màn hình báo cáo nào vào
/// bảng màn hình ⇒ tài liệu ra đời thiếu hẳn phần báo cáo ở <c>## 6. Screens To Generate</c>; rồi
/// người dùng vẫn phải gửi bảng, và tin nhắn chốt bảng lại mở cổng lần nữa ⇒ một vòng soạn thứ hai ghi đè
/// bản vừa sinh. Hai lần gọi LLM cho một tài liệu, lần đầu chắc chắn sai.
/// </para>
///
/// <para>
/// <b>Vì sao xét "bảng còn treo" chứ không xét "lượt này có bày bảng".</b> Lượt bày bảng đã tự dọn lời mời
/// rồi (<c>BAChatService.TakeOverTurn</c> thay câu của model bằng câu dẫn của bảng, nên
/// <see cref="RequirementReadinessGate.IsWriteRequirementInvite"/> false) — chốt chặn đó có sẵn và KHÔNG
/// đủ. Bảng treo theo DỰ ÁN chứ không theo lượt: nó còn nguyên trên màn hình qua F5 và qua các lượt chat
/// sau, nên cái phải hỏi là "bảng đã được gửi chưa", đúng câu hỏi mà chính panel dùng để tự ẩn/hiện.
/// Nhờ vậy cổng này cũng phủ luôn ca fail-open (model không trả nổi bảng dùng được ở lượt sau nên lượt đó
/// chạy như lượt chat thường và model mời bấm nút, trong khi bảng của lượt TRƯỚC vẫn nằm đó).
/// </para>
///
/// <para>
/// <b>Không có ngõ cụt.</b> Mọi bảng đều gửi được ngay: bỏ tích sạch vẫn là một câu trả lời hợp lệ và vẫn
/// được lưu (<c>ConfirmReportMapUseCase</c>), bảng thông báo còn dòng trống người nhận thì popup của nó bày
/// sẵn lối "Không cần gửi". Nên "cổng đóng" ở đây luôn kèm đúng một việc người dùng bấm một cái là xong —
/// khác hẳn một nút mờ-và-khóa, thứ mà repo đã cố ý bỏ đi.
/// </para>
/// </summary>
public static class PendingConfirmTableGate
{
    // Tên + nhãn nút của từng bảng. Đây là NGUỒN DUY NHẤT của các chuỗi này ngoài chính nút gửi: view
    // render chúng vào data-table-name/data-send-label của từng panel để requirements.js gọi đúng tên
    // bảng mà không phải chép lại danh sách.
    public static readonly PendingConfirmTable ColumnMap = new("bảng cột", "Gửi bảng cột");
    public static readonly PendingConfirmTable FlowMap = new("bảng luồng", "Gửi bảng luồng");
    public static readonly PendingConfirmTable EntityMap = new("bảng đối tượng", "Gửi bảng đối tượng");
    public static readonly PendingConfirmTable ReportMap = new("bảng báo cáo", "Gửi bảng báo cáo");
    public static readonly PendingConfirmTable ScreenScope = new("bảng màn hình", "Gửi bảng màn hình");
    public static readonly PendingConfirmTable PermissionMatrix = new("bảng phân quyền", "Gửi bảng phân quyền");
    public static readonly PendingConfirmTable NotificationMap = new("bảng thông báo", "Gửi bảng thông báo");

    /// <summary>
    /// Bảng đang treo, hoặc null khi không còn bảng nào chờ. Cần <see cref="Project.Conversations"/> và
    /// <see cref="Project.SourceFiles"/> đã nạp; thiếu ⇒ trả null (FAIL-OPEN, cùng luật với mọi cổng khác
    /// ở đây: chặn nhầm một vòng soạn hợp lệ đắt hơn nhiều so với để lọt một vòng).
    ///
    /// <para>
    /// Thứ tự xét là thứ tự PHỤ THUỘC của <see cref="InterviewTableGate.Select"/> (luồng → đối tượng →
    /// báo cáo → màn hình → phân quyền → thông báo), thêm bảng cột đứng đầu vì nó thuộc đầu buổi. Thường
    /// chỉ một bảng treo cùng lúc — cổng của bảng chưa chốt cứ mở lại mỗi lượt và thắng ưu tiên — nhưng
    /// đường mở lại của <see cref="ScreenScopeGate"/> làm hai bảng treo cùng lúc là chuyện có thật, và
    /// lúc đó phải gọi tên bảng người dùng sẽ được hỏi TRƯỚC.
    /// </para>
    /// </summary>
    public static PendingConfirmTable? Select(Project project)
    {
        // Cùng thứ tự ổn định (CreatedAt rồi Id) với mọi chỗ đọc hội thoại khác — CreatedAt có thể trùng.
        var turns = project.Conversations
            .Where(c => c.Role == "assistant")
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToList();

        string? LastJson(Func<AgentConversation, string?> column) =>
            turns.LastOrDefault(c => !string.IsNullOrWhiteSpace(column(c))) is { } turn ? column(turn) : null;

        // BẢNG CỘT treo theo FILE (ProjectSourceFile.ColumnMap còn null), không theo dự án: một dự án có
        // thể có file đã chốt lẫn file chưa. Lọc y hệt bản view dựng panel.
        var unconfirmedFiles = project.SourceFiles
            .Where(s => s.Kind == SourceFileKind.Spreadsheet && s.ColumnMap == null)
            .Select(s => (s.FileName ?? "").Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (unconfirmedFiles.Count > 0
            && ConversationTurnRenderer.ParseColumnMap(LastJson(c => c.ColumnMap))
                .Any(c => unconfirmedFiles.Contains((c.FileName ?? "").Trim())))
            return ColumnMap;

        if (project.FlowMap == null && ConversationTurnRenderer.ParseFlowMap(LastJson(c => c.FlowMap)).Count > 0)
            return FlowMap;

        if (project.EntityMap == null && ConversationTurnRenderer.ParseEntityMap(LastJson(c => c.EntityMap)).Count > 0)
            return EntityMap;

        if (project.ReportMap == null && ConversationTurnRenderer.ParseReportMap(LastJson(c => c.ReportMap)).Count > 0)
            return ReportMap;

        // Bảng màn hình là cổng DUY NHẤT mở lại được, nên "dự án đã chốt bảng chưa" không trả lời được câu
        // hỏi này: ở lượt bày LẠI thì cột trên Project đã khác null từ lần chốt trước. Phép so đúng là bản
        // đã chốt với chính bảng server vừa bày — cùng hàm mà view dùng để dựng panel.
        if (ScreenScopeMapBuilder.PendingRows(project.ScreenScopeMap, LastJson(c => c.ScreenScopeMap)).Count > 0)
            return ScreenScope;

        if (project.PermissionMatrix == null
            && ConversationTurnRenderer.ParsePermissionMatrix(LastJson(c => c.PermissionMatrix)).Count > 0)
            return PermissionMatrix;

        if (project.NotificationMap == null
            && ConversationTurnRenderer.ParseNotificationMap(LastJson(c => c.NotificationMap)).Count > 0)
            return NotificationMap;

        return null;
    }
}
