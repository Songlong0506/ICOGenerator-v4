using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// HÌNH DẠNG của lượt trả lời đang được nắn: nội dung, chip, thẻ hỏi gộp và sáu bảng chốt. Đây là thứ mà
/// các chốt chặn TẤT ĐỊNH của <see cref="BAChatService"/> viết đè lên nhau trước khi lượt được lưu.
///
/// <para>
/// Vì sao gom thành MỘT vật thay vì tám biến cục bộ: các chốt chặn không sửa một trường lẻ, chúng thay
/// TRỌN lượt — thay nội dung thì phải hạ luôn chip, cờ "câu mở" và thẻ hỏi, nếu không màn hình có hai lượt
/// hỏi chồng lên nhau, hoặc một câu hỏi đóng đứng cạnh đúng bộ nút của câu hỏi TRƯỚC. Gói lại ở đây thì
/// mỗi phép thay lượt là MỘT lời gọi làm đủ mọi việc, thay vì bốn dòng gán mà người sửa sau phải nhớ cho
/// đủ — và quên một dòng là đúng loại lỗi không lộ ra ở build lẫn ở review.
/// </para>
/// </summary>
internal sealed class BAChatTurnDraft
{
    public string Reply { get; set; } = string.Empty;

    public string? SuggestionsJson { get; set; }

    public bool SuggestionsMultiSelect { get; set; }

    /// <summary>
    /// Lượt hỏi MỘT câu MỞ (xin lời kể): không có chip, UI mời gõ vào ô nhập. Các chốt chặn TẤT ĐỊNH đều
    /// thay lượt bằng câu hỏi đóng có sẵn phương án, nên chúng phải hạ cờ này — để sót thì màn hình mời
    /// "kể tự do" ngay dưới một hàng chip.
    /// </summary>
    public bool OpenEnded { get; set; }

    /// <summary>
    /// Model khai lượt này là nhịp TÓM TẮT KIỂM CHỨNG — xem <see cref="Contracts.Requirements.BAChatReply.SummaryCheck"/>.
    /// Chỉ dùng để BÙ bộ hai chip khi lượt thiếu chip; mọi đường thay trọn lượt bên dưới đều hạ nó xuống,
    /// vì lúc đó nội dung không còn là bản tóm tắt model vừa viết nữa.
    /// </summary>
    public bool SummaryCheck { get; set; }

    /// <summary>
    /// Lượt này MỜI người dùng bấm "Write Requirement" — <b>tín hiệu DUY NHẤT</b> mở cổng tạo tài liệu.
    /// Gốc là cờ <see cref="Contracts.Requirements.BAChatReply.Ready"/> do model khai, nhưng nó chỉ là ĐỀ
    /// NGHỊ: <c>BAChatService.ApplyReadinessGate</c> đối chiếu với bản đồ bao phủ rồi mới cho đứng, và mọi
    /// đường thay trọn lượt bên dưới đều hạ nó xuống.
    ///
    /// <para>
    /// <b>Trước đây cờ này được SUY RA bằng cách dò chuỗi "Write Requirement" trong nội dung lượt.</b> Cái
    /// giá của phép dò đó: nội dung lượt là văn xuôi model tự viết, nên chỉ cần nó đổi cách diễn đạt (hoặc
    /// nhắc tới nút để nói rằng CHƯA nên bấm) là cổng đọc sai — mà không có gì trong hệ thống báo. Cờ khai
    /// tường minh cắt hẳn chuyện đó: nút mở theo một trường boolean, không theo mặt chữ của một câu tiếng
    /// Việt. Đổi lại, nó KHÔNG được tin một mình — xem chốt chặn ở <c>ApplyReadinessGate</c>.
    /// </para>
    /// </summary>
    public bool InvitesWriteRequirement { get; set; }

    public List<BAChatQuestion> Questions { get; set; } = new();

    public List<PermissionMatrixRow> PermissionMatrix { get; set; } = new();

    public List<FlowMapRow> FlowMap { get; set; } = new();

    public List<ScreenScopeRow> ScreenScopeMap { get; set; } = new();

    public List<EntityMapRow> EntityMap { get; set; } = new();

    public List<ReportMapRow> ReportMap { get; set; } = new();

    public List<NotificationMapRow> NotificationMap { get; set; } = new();

    public List<string> UncoveredFlowSteps { get; set; } = new();

    /// <summary>
    /// Lượt này có chở một BẢNG CHỐT nào không — chỗ trả lời duy nhất của lượt, nên mọi chốt chặn về
    /// "chỗ trả lời" đều phải nhường nó.
    /// </summary>
    public bool CarriesTable
        => PermissionMatrix.Count > 0
           || FlowMap.Count > 0
           || ScreenScopeMap.Count > 0
           || EntityMap.Count > 0
           || ReportMap.Count > 0
           || NotificationMap.Count > 0;

    /// <summary>
    /// LƯỢT CÂM: không chip, không thẻ hỏi, không bảng, không dấu hỏi, không nhắc tới nút — người dùng
    /// không có chỗ nào để trả lời.
    ///
    /// <para>
    /// <see cref="OpenEnded"/> KHÔNG mua được quyền miễn trừ ở đây, và đó là chỗ chốt chặn này từng thủng.
    /// Cờ đó do model tự đặt, còn cái nó bật lên chỉ là một Ô NHẬP — mà ô nhập thì lượt nào cũng có. Ca
    /// thật (dự án JD Libary 5, lượt 18): *"Để mình tổng hợp lại những gì đã chốt và hỏi thêm một số điểm
    /// còn lại nhé."* — không chip, không thẻ hỏi, không dấu hỏi, đúng hình dạng lượt câm mà prompt cấm
    /// bằng tên ("KHÔNG kết bằng lời hứa về một bước bạn sắp làm"), nhưng model kèm <c>openEnded: true</c>
    /// nên nó đi thẳng qua chốt chặn này. Người dùng đáp "ok" và nhận lại một lượt nữa: đúng vòng lặp mà
    /// cả class test kia sinh ra để cắt.
    /// </para>
    ///
    /// <para>
    /// Thứ MỞ được chỗ trả lời là bản thân lượt có HỎI hay có NHỜ, không phải cái cờ đi kèm — nên phép thử
    /// đọc chính nội dung: dấu hỏi, hoặc một lời nhờ hành động (xin file) vốn cố ý không có dấu hỏi.
    /// </para>
    /// </summary>
    public bool IsSilent
        => string.IsNullOrEmpty(SuggestionsJson)
           && Questions.Count == 0
           && !Reply.Contains('?', StringComparison.Ordinal)
           && !Reply.Contains('\uff1f', StringComparison.Ordinal)
           && !SourceRequestTurn.Looks(Reply)
           // Lượt MỜI bấm nút không câm: chỗ trả lời của nó là cái nút thật ở cổng tạo tài liệu.
           && !InvitesWriteRequirement
           // Lượt có BẢNG không câm: bảng chính là chỗ trả lời DUY NHẤT của lượt, và câu dẫn của nó cố
           // tình không phải câu hỏi (xem TakeOverForTable).
           && !CarriesTable;

    /// <summary>
    /// Thay TRỌN lượt bằng một câu do CƠ CHẾ soạn (bước kế tất định, câu chặn của cổng readiness, lời nhờ
    /// gửi file). Nội dung mới luôn đi kèm việc dọn sạch chip và thẻ hỏi gộp của lượt cũ: để sót một trong
    /// hai là bày ra một câu hỏi kèm đúng bộ nút của câu hỏi TRƯỚC.
    /// </summary>
    /// <param name="invites">
    /// Câu thay vào CÓ PHẢI lời mời bấm "Write Requirement" không. Mặc định <c>false</c> vì phần lớn đường
    /// thay lượt là câu chặn/câu hỏi; chỉ bước kế tất định (<c>BuildFollowUpAfterRepeat</c>, khi bản đồ đã
    /// đủ) mới soạn ra một lời mời và phải nói ra ở đây — im lặng thì lượt đó lên màn hình mời bấm nút
    /// trong khi cổng vẫn đóng.
    /// </param>
    public void Replace(string reply, bool openEnded, bool invites = false)
    {
        Reply = reply;
        SuggestionsJson = null;
        SuggestionsMultiSelect = false;
        OpenEnded = openEnded;
        SummaryCheck = false;
        InvitesWriteRequirement = invites;
        Questions = new List<BAChatQuestion>();
    }

    /// <summary>
    /// Bảng dựng được thì nó là chỗ trả lời DUY NHẤT của lượt: dọn chip và thẻ hỏi gộp. Chip bấm là GỬI
    /// NGAY, nên để cả hai cùng sống thì một cú bấm nhầm cuốn mất lượt trước khi người dùng chọn xong
    /// bảng — và bảng thì không bao giờ được chốt. Cùng luật với bảng cột.
    ///
    /// <para>
    /// Câu dẫn của model chỉ được dùng khi lượt đó KHÔNG khai <c>ready</c>: một lời mời đặt trên đầu bảng
    /// bảo người dùng bấm nút, trong khi việc thật sự phải làm nằm ở bảng ngay dưới — đúng kiểu "câu hỏi
    /// không có nút trả lời" mà lượt đọc file đã vấp. Lượt có bảng thì <b>không bao giờ</b> là lời mời:
    /// bảng là chỗ trả lời duy nhất của lượt, nên cờ bị hạ ở đây bất kể model khai gì.
    /// </para>
    /// </summary>
    /// <param name="force">
    /// Câu dẫn của CƠ CHẾ thắng câu của model, dùng cho lượt bày lại bảng màn hình: model không biết lượt
    /// này là lượt bổ sung nên câu nó viết ra mời rà lại cả bảng đã chốt.
    /// </param>
    /// <param name="modelInvites">
    /// Cờ <c>ready</c> của chính lượt model vừa trả về. true ⇒ câu dẫn của model là một lời mời bấm nút,
    /// không dùng được làm câu dẫn bảng.
    /// </param>
    public void TakeOverForTable(string fallbackIntro, string? modelMessage, bool modelInvites, bool force = false)
    {
        Reply = force
                || string.IsNullOrWhiteSpace(modelMessage)
                || modelInvites
            ? fallbackIntro
            : EndpointQuirks.StripInternalNotices(modelMessage);
        SuggestionsJson = null;
        SuggestionsMultiSelect = false;
        OpenEnded = false;
        SummaryCheck = false;
        InvitesWriteRequirement = false;
        Questions = new List<BAChatQuestion>();
    }

    /// <summary>
    /// Gắn bộ chip DỰ PHÒNG cho một lượt là câu hỏi ĐÓNG: lượt nào chỉ cần gật hoặc đính chính thì phải
    /// có nút để bấm, thiếu nút là bắt người dùng gõ tay một câu xác nhận.
    /// </summary>
    public void SetFallbackSuggestions(IReadOnlyList<string> suggestions)
    {
        SuggestionsJson = JsonSerializer.Serialize(suggestions);
        SuggestionsMultiSelect = false;
        OpenEnded = false;
    }
}
