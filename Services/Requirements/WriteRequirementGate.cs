using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Trạng thái của CỔNG TẠO TÀI LIỆU (<c>#writeReqZone</c> ở cuối khung chat) — chỗ DUY NHẤT có nút "Write
/// Requirement". Năm trạng thái loại trừ nhau, xét theo đúng thứ tự ưu tiên: đang chạy &gt; còn bảng chờ chốt
/// &gt; đã soạn &amp; chưa có gì mới &gt; sẵn sàng &gt; còn phải khai thác thêm. Chỉ <see cref="Ready"/> có nút.
///
/// <para>
/// Hàm thuần, suy TẤT ĐỊNH từ hội thoại + các run soạn tài liệu + bản Brief + bảng còn treo — không thêm cột
/// trạng thái nào, để khỏi sinh nguồn sự thật thứ hai. Từng nằm thẳng trong <c>Index.cshtml</c>; tách ra đây
/// để luật "cặp hỏi–đáp ngoài phạm vi không làm cổng nhúc nhích" (<see cref="OffTopicExchange"/>) có test giữ.
/// <c>requirements.js</c> không suy lại trạng thái này: nó khởi tạo từ bản server render (<c>data-state</c>)
/// và chỉ nắn theo cờ mời của frame done.
/// </para>
/// </summary>
public static class WriteRequirementGate
{
    /// <summary>Chưa đủ thông tin. KHÔNG dựng nút nào (một nút mờ-và-khóa chỉ mời bấm rồi từ chối); panel "Tiến độ khai thác" là chỗ nói điều còn thiếu.</summary>
    public const string Waiting = "waiting";

    /// <summary>Còn một bảng chốt đang chờ gửi (<see cref="PendingConfirmTableGate"/>). Cổng đóng; <c>#tableGate</c> nói ra bảng nào + nút nào khi <see cref="State.TableGateSpeaks"/>.</summary>
    public const string Table = "table";

    /// <summary>Lượt BA mới nhất mời tạo tài liệu và không còn bảng nào chờ chốt — trạng thái DUY NHẤT có nút.</summary>
    public const string Ready = "ready";

    /// <summary>Vòng soạn đang xếp hàng/đang chạy. Tiến độ đã có panel <c>.workflow-progress</c> trong khung chat.</summary>
    public const string Running = "running";

    /// <summary>Đã soạn xong và người dùng chưa nói thêm gì VỀ DỰ ÁN kể từ đó. Cổng đóng hẳn.</summary>
    public const string Done = "done";

    /// <summary>Kết quả của <see cref="Resolve"/>.</summary>
    /// <param name="Value">Một trong năm hằng trạng thái ở trên — ghi vào <c>data-state</c> của wrapper.</param>
    /// <param name="RequirementReady">Lượt BA mới nhất là lời mời đã qua cổng readiness (cột <c>ReadinessVerified</c>).</param>
    /// <param name="DraftSettled">Bản Brief đã có, vòng soạn gần nhất đã xong, và chưa có lượt user nào về dự án kể từ đó.</param>
    /// <param name="TableGateSpeaks"><c>#tableGate</c> lên tiếng: cổng đang chặn vì bảng VÀ lẽ ra nó đã mở.</param>
    public sealed record State(string Value, bool RequirementReady, bool DraftSettled, bool TableGateSpeaks)
    {
        public bool IsOpen => Value == Ready;
    }

    /// <param name="conversations">Hội thoại hiện hành (mọi thứ tự — hàm tự sắp CreatedAt rồi Id).</param>
    /// <param name="workflowRuns">Các workflow run của dự án; chỉ run soạn tài liệu được xét.</param>
    /// <param name="briefExists">Đã có một bản Product Brief bất kỳ — <c>draft</c> HOẶC bản đã duyệt <c>V{n}</c>.</param>
    /// <param name="tablePending">Còn bảng chốt chờ gửi (<see cref="PendingConfirmTableGate.Select"/> khác null).</param>
    public static State Resolve(
        IEnumerable<AgentConversation> conversations,
        IEnumerable<WorkflowRun> workflowRuns,
        bool briefExists,
        bool tablePending)
    {
        // Cặp hỏi–đáp NGOÀI PHẠM VI không tồn tại với cổng — xem OffTopicExchange cho ca thật: một câu hỏi
        // giá vàng sau lần soạn Brief từng đủ để kéo cổng ra khỏi "done" và mở lại nút, dù không có gì về dự
        // án thay đổi. Lọc MỘT lần ở đây thì cả hai câu hỏi phía dưới ("lượt BA cuối là gì", "người dùng
        // nói thêm gì từ lần soạn trước chưa") cùng nhìn qua cùng một phép lọc.
        var turns = OffTopicExchange.Substantive(conversations)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToList();

        // Cổng MỞ khi lượt BA mới nhất MỜI tạo tài liệu. Tín hiệu là CỘT ReadinessVerified — đóng dấu một lần
        // lúc lưu lượt, từ cờ `ready` model khai đã đối chiếu với bản đồ bao phủ. KHÔNG dò chuỗi "Write
        // Requirement" trong nội dung lượt: một lượt nhắc tới nút để nói rằng CHƯA nên bấm cũng sẽ mở cổng.
        //
        // KHÔNG CÓ ĐƯỜNG MỞ NÀO KHÁC. Từng có "draft đã có + bản đồ đủ ⇒ mở" bất kể lượt BA cuối nói gì — nó
        // mở cổng đúng ở lượt BA đang HỎI. Bất biến: cổng mở ⇒ không còn câu hỏi nào của BA đang chờ.
        var requirementReady = turns.LastOrDefault(c => c.Role == "assistant")?.ReadinessVerified ?? false;

        var draftRuns = workflowRuns
            .Where(r => r.Name == WorkflowOrchestrator.RequirementDraftRunName)
            .ToList();

        // ĐANG SOẠN ⇒ khoá nút. Thiếu nhánh này thì bấm xong → redirect → trang render lại với lời mời vẫn ở
        // cuối hội thoại → nút sáng lại, và mỗi lần bấm thêm là một run sinh lại đúng bản draft đó.
        var draftRunInFlight = draftRuns.Any(r => r.Status is WorkflowRunStatus.Queued or WorkflowRunStatus.Running);

        // ĐÃ SOẠN XONG VÀ CHƯA CÓ GÌ MỚI ⇒ cổng ĐÓNG HẲN. Panel workflow ngay phía trên đã nói "Tài liệu đã
        // sẵn sàng", thứ đáng rà lúc này là chính Product Brief (ghim ghi chú trên bản xem trước), và bấm lại
        // khi chưa bổ sung gì chỉ tốn 2–3 lời gọi LLM để GHI ĐÈ bản đang có bằng một bản gần giống — có thể
        // tệ hơn. Người dùng nhắn thêm một câu VỀ DỰ ÁN là cổng rời trạng thái này; một câu ngoài phạm vi thì
        // không (đã lọc ở trên).
        //
        // "Đã có bản Brief" tính CẢ bản đã duyệt V{n}: Approve ĐỔI TÊN bản draft thành V{n}, nên chỉ đếm
        // "draft" thì ngay sau khi duyệt trạng thái rơi về "ready" và nút hiện lại dưới panel dựng POC.
        var lastFinishedDraftRunAt = draftRuns
            .Where(r => r.Status == WorkflowRunStatus.Completed && r.FinishedAt.HasValue)
            .Max(r => (DateTime?)r.FinishedAt!.Value);
        var lastUserTurnAt = turns.LastOrDefault(c => c.Role == "user")?.CreatedAt;
        var draftSettled = briefExists
            && lastFinishedDraftRunAt.HasValue
            && (lastUserTurnAt == null || lastUserTurnAt <= lastFinishedDraftRunAt.Value);

        // CÒN BẢNG CHỜ CHỐT ⇒ cổng ĐÓNG, kể cả khi lượt mới nhất là lời mời (ca fail-open: bảng của lượt
        // TRƯỚC còn treo, lượt này model mời bấm nút — bấm thì tài liệu ra đời thiếu đúng phần cái bảng chở).
        // Đứng TRƯỚC "done" vì một bảng chưa chốt là việc còn phải làm, kể cả khi bản Brief vừa xong.
        var value = draftRunInFlight ? Running
            : tablePending ? Table
            : draftSettled ? Done
            : requirementReady ? Ready
            : Waiting;

        // #tableGate CHỈ NÓI KHI CỔNG LẼ RA ĐÃ MỞ: ở lượt BA vừa bày bảng và chưa mời gì, cổng vốn đã đóng im
        // lặng, và một bong bóng ngay dưới cái bảng đang có sẵn nút gửi là lần thứ hai nói cùng một việc.
        var tableGateSpeaks = value == Table && !draftSettled && requirementReady;

        return new State(value, requirementReady, draftSettled, tableGateSpeaks);
    }
}
