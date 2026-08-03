namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// MỘT câu hỏi trong lượt hỏi GỘP của BA (2–4 câu cùng lúc). Trước đây một lượt chat chỉ chở được đúng
/// một câu hỏi (<c>message</c> + <c>suggestions</c>), nên phỏng vấn đủ 12 nhóm bản đồ bao phủ tốn hàng
/// chục lượt đi-về — chính chỗ mà cổng "chốt nhanh" từng cắt bằng cách để BA tự soạn phương án rồi ghi
/// vào hội thoại như lời người dùng. Nay BA vẫn PHỎNG VẤN như cũ (vẫn là người hỏi, người dùng vẫn là
/// người trả lời), chỉ được phép gộp các câu hỏi ĐỘC LẬP vào một lượt để rút số vòng đi-về.
///
/// <para>
/// Ranh giới "được gộp / không được gộp" nằm ở prompt <c>requirement-chat.v4.md</c> và là phần cốt tử:
/// chỉ gộp khi câu trả lời của câu này KHÔNG làm đổi câu hỏi kế tiếp. Mọi câu hỏi đào sâu (xin câu
/// chuyện thật, đào ngoại lệ, chốt ví dụ số, chốt kịch bản luồng, gỡ mâu thuẫn, nhịp tóm tắt kiểm
/// chứng) vẫn phải đứng MỘT MÌNH — gộp chúng là mất đúng cái phễu mở → đào sâu → chốt.
/// </para>
/// </summary>
public class BAChatQuestion
{
    /// <summary>
    /// Nhãn nhóm bản đồ bao phủ mà câu hỏi này nhắm tới (vd "Thông báo / nhắc nhở"). Chỉ để hiển thị
    /// như tiêu đề nhỏ trên thẻ hỏi — hệ thống KHÔNG ghép trạng thái bản đồ theo trường này (bản đồ vẫn
    /// do <see cref="ICOGenerator.Services.Requirements.RequirementCoverageService"/> chắt lọc từ hội
    /// thoại). Rỗng thì thẻ chỉ hiện câu hỏi.
    /// </summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Câu hỏi, viết đủ nghĩa để đứng một mình — nó được ghi lại nguyên văn vào lượt trả lời của người dùng.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>Đáp án gợi ý NGẮN để bấm chọn. Bắt buộc có (mọi câu hỏi đều phải kèm gợi ý), ngoài ra UI luôn có ô tự nhập.</summary>
    public List<string> Suggestions { get; set; } = new();

    /// <summary>True khi câu hỏi này cho phép chọn NHIỀU gợi ý cùng lúc (vd "gồm những vai trò nào?").</summary>
    public bool MultiSelect { get; set; }
}
