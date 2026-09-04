using System.ComponentModel;

namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Hình dạng JSON của "Bản đồ bao phủ yêu cầu" — format LƯU TRỮ trên <c>Project.RequirementCoverageMap</c>
/// và một nửa schema structured output của lượt distill (<see cref="CoverageDistillDocument"/>).
/// <para>
/// Bản đồ chỉ chở TRẠNG THÁI: nhóm nào đã rõ, đã ghi nhận được gì, dựa vào câu nào. CÂU HỎI còn phải hỏi
/// nằm ở danh sách riêng (<see cref="OpenQuestionDocument"/>, lưu trên <c>Project.OpenQuestions</c>) — xem
/// class đó cho lý do một nhóm cần nhiều hơn một câu hỏi.
/// </para>
/// <para>
/// Tên thuộc tính để ASCII có chủ đích: schema này được gửi thẳng cho model qua
/// <c>response_format: json_schema</c>, và nghĩa của từng trường thì nằm ở <see cref="DescriptionAttribute"/>
/// + prompt, chỗ diễn đạt được đầy đủ hơn một cái tên. Trạng thái vẫn giữ nguyên bốn nhãn tiếng Việt vì
/// chúng là từ vựng nghiệp vụ đã ghim trong prompt, trong DB và trên màn hình.
/// </para>
/// </summary>
public class CoverageMapDocument
{
    [Description("Đúng 12 nhóm thông tin, giữ nguyên thứ tự và tên nhóm của checklist.")]
    public List<CoverageMapEntry> Items { get; set; } = new();
}

/// <summary>
/// Kết quả MỘT lời gọi chắt lọc của <c>RequirementCoverageService</c>: bản đồ trạng thái + danh sách câu
/// hỏi, cùng đọc một hội thoại và cùng ghi ra trong một lượt.
/// <para>
/// <b>Vì sao một lời gọi chứ không hai.</b> Hai danh sách này ràng buộc nhau chặt tới mức chúng chỉ đúng
/// khi được viết cùng nhau: một nhóm còn câu hỏi MỞ thì dòng của nó không được <c>[RÕ]</c>. Khi chúng do
/// hai lời gọi khác nhau chắt ra — bản đồ trong lượt chat, danh sách câu hỏi ở hậu kỳ — thì danh sách
/// luôn cũ hơn bản đồ đúng một lượt, và cổng "Write Requirement" bày ra một câu hỏi người dùng vừa trả
/// lời xong. Ghi chung một lượt thì độ trễ đó biến mất, và cùng với nó là cả tầng hoà giải hai bên.
/// </para>
/// </summary>
public class CoverageDistillDocument
{
    [Description("Đúng 12 nhóm thông tin, giữ nguyên thứ tự và tên nhóm của checklist.")]
    public List<CoverageMapEntry> Items { get; set; } = new();

    [Description("Mọi câu hỏi của cuộc phỏng vấn: mục còn phải hỏi (MỞ) và mục đã được trả lời (ĐÃ TRẢ LỜI). Một nhóm được phép có nhiều câu hỏi.")]
    public List<OpenQuestionEntry> Questions { get; set; } = new();
}

/// <summary>Một nhóm thông tin trong <see cref="CoverageMapDocument"/>.</summary>
public class CoverageMapEntry
{
    [Description("Tên nhóm, chép đúng từ checklist.")]
    public string Label { get; set; } = string.Empty;

    [Description("Nhóm cốt lõi (★) hay không.")]
    public bool Core { get; set; }

    [Description("Một trong: RÕ | MỘT PHẦN | CHƯA HỎI | KHÔNG ÁP DỤNG")]
    public string Status { get; set; } = string.Empty;

    [Description("Tóm tắt RẤT NGẮN điều đã biết về nhóm này. Rỗng khi CHƯA HỎI.")]
    public string Known { get; set; } = string.Empty;

    [Description("Trích NGUYÊN VĂN, ngắn, lời người dùng hoặc câu trong tài liệu nguồn mà kết luận dựa vào. Bắt buộc khi RÕ hoặc MỘT PHẦN. Không diễn đạt lại.")]
    public string Evidence { get; set; } = string.Empty;
}
