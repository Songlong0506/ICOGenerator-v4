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

    /// <summary>
    /// Đáp án gợi ý NGẮN để bấm chọn. Bắt buộc có với câu ĐÓNG (đáp án nằm trong một tập hữu hạn);
    /// RỖNG khi <see cref="OpenEnded"/> — xem ghi chú ở đó. Ngoài ra UI luôn có ô tự nhập.
    /// </summary>
    public List<string> Suggestions { get; set; } = new();

    /// <summary>True khi câu hỏi này cho phép chọn NHIỀU gợi ý cùng lúc (vd "gồm những vai trò nào?").</summary>
    public bool MultiSelect { get; set; }

    /// <summary>
    /// Câu hỏi MỞ: đáp án thật là một lời kể/mô tả của người dùng, không rút gọn được thành vài chip.
    /// Khi true thì <see cref="Suggestions"/> phải RỖNG và UI mở sẵn ô tự nhập thay cho hàng gợi ý.
    ///
    /// <para>
    /// Vì sao cần một cờ riêng thay vì cứ để BA kèm gợi ý cho mọi câu: với câu mở, chip không phải tiện
    /// ích mà là một cái BẪY. Ví dụ thật — *"kể giúp lần gần nhất lập kế hoạch lớp học: bắt đầu từ đâu,
    /// làm những bước nào, kết quả cuối cùng là gì?"* kèm chip `["Đã có danh sách khóa học", "Bắt đầu từ
    /// nhu cầu đào tạo", "Đang theo dõi bằng Excel"]`. Mỗi chip chỉ trả lời được MỘT MẨU (câu "bắt đầu
    /// từ đâu"), nhưng ở lượt hỏi một câu thì bấm chip là GỬI NGAY: cả câu chuyện — các bước và kết quả,
    /// tức đúng thứ đáng giá nhất của câu hỏi — rơi mất, mà bản đồ bao phủ và "Điều đã chốt" lại ghi
    /// nhận mẩu bốn chữ đó như câu trả lời thật của người dùng. Đây là cùng một lỗi với "câu hỏi kép mà
    /// bộ chip chỉ trả lời được một nửa" (xem requirement-chat.v4.md), chỉ khác là nửa bị bỏ rơi lớn hơn.
    /// </para>
    /// </summary>
    public bool OpenEnded { get; set; }
}
