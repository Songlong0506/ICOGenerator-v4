namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Quy tắc TẤT ĐỊNH chọn hình dạng ngữ cảnh cho một lượt chat BA: PHỎNG VẤN (<c>requirement-chat.v4.md</c>
/// + bản đồ bao phủ + các bảng đã chốt + bối cảnh tổ chức đầy đủ + toàn bộ hội thoại chưa gộp) hay YÊU CẦU
/// THAY ĐỔI (<c>requirement-change.v1.md</c> + Brief đã duyệt + tóm tắt + mục lục màn hình + các lượt sau
/// mốc duyệt).
///
/// <para>
/// Vì sao có chế độ thứ hai: sau khi người dùng Approve một Product Brief, mọi lượt chat là một yêu cầu
/// THAY ĐỔI trên bản đã ký, không còn là buổi phỏng vấn. Prompt phỏng vấn (~100.000 ký tự: lượt mở đầu,
/// xin file, hỏi hiện tại rồi cải tiến, đề xuất ý tưởng, ngân sách lượt…) gần như không còn luật nào áp
/// dụng, còn bản đồ bao phủ và bốn bảng đã chốt chỉ chở lại đúng điều Brief đã ghi. Đo trên log BAChat
/// 2026-11-20 (lượt người dùng xin thêm màn hình Tòa nhà/Khu vực sau POC): 97k token prompt, trong khi bản
/// Brief đã duyệt — thứ DUY NHẤT nói được "điều này đã có hay chưa" — lại không có mặt.
/// </para>
///
/// <para>
/// Hai điều kiện, thiếu một là về chế độ phỏng vấn như cũ:
/// <list type="bullet">
///   <item>Có Brief ĐÃ DUYỆT và mốc duyệt (<c>Project.BriefApprovedTurnCount</c>) đã đóng.</item>
///   <item>Lượt này KHÔNG bày bảng: luật của bảng luồng (<c>table-flow-map.v1.md</c>) viết cho prompt phỏng
///   vấn, và một lượt bày bảng sau mốc duyệt nghĩa là người dùng vừa kể một luồng mới đủ lớn để phải rà
///   lại — đúng việc của chế độ phỏng vấn.</item>
/// </list>
/// </para>
///
/// <para>
/// Các lượt soạn ngầm (bản đồ bao phủ, phạm vi màn hình, bảng phân quyền) và mọi chốt chặn sau lời gọi
/// model chạy Y HỆT ở cả hai chế độ — chế độ chỉ đổi thứ được GỬI cho model chat.
/// </para>
/// </summary>
public static class RequirementChangeMode
{
    /// <summary>Đã có Brief đã duyệt và mốc duyệt — điều kiện để gộp phần hội thoại trước mốc vào tóm tắt.</summary>
    public static bool HasApprovedBaseline(ProjectDocumentLookup.ApprovedDocument? approvedBrief, int briefApprovedTurnCount)
        => approvedBrief != null && briefApprovedTurnCount > 0;

    /// <summary>Lượt này dùng ngữ cảnh "yêu cầu thay đổi".</summary>
    public static bool IsActive(ProjectDocumentLookup.ApprovedDocument? approvedBrief, int briefApprovedTurnCount, InterviewTableKind table)
        => HasApprovedBaseline(approvedBrief, briefApprovedTurnCount) && table == InterviewTableKind.None;
}
