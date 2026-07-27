namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Một phương án mặc định BA soạn sẵn cho MỘT nhóm thông tin còn thiếu trong bản đồ bao phủ, để người
/// dùng duyệt hàng loạt thay vì trả lời từng câu qua nhiều lượt chat (xem
/// <c>Prompts/BusinessAnalyst/gap-proposals.v1.md</c>). <see cref="Group"/> chép nguyên văn nhãn nhóm
/// trong bản đồ nên UI ghép được phương án với dòng tiến độ tương ứng.
/// </summary>
public class GapProposal
{
    public string Group { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Proposal { get; set; } = string.Empty;
}

/// <summary>Bộ phương án cho MỌI nhóm còn thiếu — shape structured-output của lượt gọi LLM.</summary>
public class GapProposalSet
{
    public List<GapProposal> Proposals { get; set; } = new();
}

/// <summary>
/// Một dòng người dùng đã CHỐT ở cổng "chốt nhanh phần còn lại": nhóm thông tin + nội dung được chốt
/// (bản đề xuất giữ nguyên, hoặc bản người dùng gõ đè). Ghi vào hội thoại như lời của chính người dùng —
/// đó là điều biến một phương án thành yêu cầu đã chốt, giữ nguyên nguyên tắc "tài liệu không tự giả định".
/// </summary>
public class GapDecision
{
    public string Group { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
}
