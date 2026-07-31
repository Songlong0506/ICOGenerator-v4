namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Một phương án mặc định BA soạn sẵn cho MỘT nhóm thông tin còn thiếu trong bản đồ bao phủ, để người
/// dùng duyệt hàng loạt thay vì trả lời từng câu qua nhiều lượt chat (xem
/// <c>Prompts/BusinessAnalyst/gap-proposals.v2.md</c>). <see cref="Group"/> chép nguyên văn nhãn nhóm
/// trong bản đồ nên UI ghép được phương án với dòng tiến độ tương ứng.
///
/// <para>
/// <see cref="Confidence"/>/<see cref="Basis"/> là phần cốt tử của cổng này: khi hội thoại còn ngắn, BA
/// KHÔNG có căn cứ cho phần lớn nhóm, và bản đầu tiên của cổng vẫn trả về một phương án nghe rất chắc
/// chắn cho cả 11 nhóm — người dùng phải đọc rồi gõ đè gần như từng dòng, tốn hơn hẳn trả lời từng câu
/// trong chat. Nay mỗi phương án phải tự khai là SUY RA từ điều người dùng đã nói (kèm trích căn cứ) hay
/// chỉ là PHỎNG ĐOÁN theo thông lệ; UI chỉ chọn sẵn nhóm suy ra, nhóm phỏng đoán để trống cho người dùng
/// chọn <see cref="Options"/> bằng một cú bấm hoặc bỏ qua.
/// </para>
/// </summary>
public class GapProposal
{
    /// <summary>Giá trị <see cref="Confidence"/> nghĩa là "có căn cứ từ điều người dùng đã nói".</summary>
    public const string Grounded = "suy-ra";

    /// <summary>Giá trị <see cref="Confidence"/> nghĩa là "BA đoán theo thông lệ, chưa có căn cứ".</summary>
    public const string Guess = "phỏng-đoán";

    public string Group { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Proposal { get; set; } = string.Empty;

    /// <summary>Trích ngắn điều người dùng/tài liệu đã nói mà phương án dựa vào. Rỗng ⇒ phỏng đoán.</summary>
    public string Basis { get; set; } = string.Empty;

    /// <summary><see cref="Grounded"/> hoặc <see cref="Guess"/> — đã chuẩn hoá ở GapProposalService.</summary>
    public string Confidence { get; set; } = string.Empty;

    /// <summary>
    /// 2–3 phương án thay thế NGẮN cho cùng câu hỏi, để người dùng đổi ý bằng một cú bấm thay vì gõ tay.
    /// Đây là câu trả lời trực tiếp cho "phải nhập tay sửa lại từng câu" — thứ làm cổng chậm hơn cả chat.
    /// </summary>
    public List<string> Options { get; set; } = new();

    // Static (không phải property) để không lọt vào JSON schema sinh từ chính lớp này ở đường structured
    // output — thêm một trường model phải điền là thêm một chỗ nó điền sai.
    public static bool IsGrounded(GapProposal proposal) =>
        string.Equals(proposal.Confidence, Grounded, StringComparison.Ordinal);
}

/// <summary>Bộ phương án cho MỌI nhóm còn thiếu — shape structured-output của lượt gọi LLM.</summary>
public class GapProposalSet
{
    public List<GapProposal> Proposals { get; set; } = new();
}

/// <summary>
/// Một dòng người dùng đã CHỐT ở cổng "chốt nhanh phần còn lại": nhóm thông tin + nội dung được chốt
/// (bản đề xuất giữ nguyên, một lựa chọn đã bấm, hoặc bản người dùng gõ đè). Ghi vào hội thoại như lời
/// của chính người dùng — đó là điều biến một phương án thành yêu cầu đã chốt, giữ nguyên nguyên tắc
/// "tài liệu không tự giả định". Nhóm người dùng để lại (không chốt) đơn giản là KHÔNG có mặt ở đây:
/// nó ở nguyên trạng thái trống trên bản đồ và BA hỏi tiếp trong chat.
/// </summary>
public class GapDecision
{
    public string Group { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
}
