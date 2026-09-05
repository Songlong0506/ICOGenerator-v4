using System.ComponentModel;

namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Hình dạng JSON của "Bản đồ bao phủ yêu cầu" — format LƯU TRỮ trên <c>Project.RequirementCoverageMap</c>
/// và một nửa schema structured output của lượt distill (<see cref="CoverageDistillDocument"/>).
/// <para>
/// Bản đồ chỉ chở TRẠNG THÁI: nhóm nào đã rõ và đã ghi nhận được những gì. CÂU HỎI còn phải hỏi
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
/// hỏi + danh sách ví dụ đã xác nhận, cùng đọc một hội thoại và cùng ghi ra trong một lượt.
/// <para>
/// <b>Vì sao một lời gọi chứ không hai.</b> Bản đồ và danh sách câu hỏi ràng buộc nhau chặt tới mức chúng
/// chỉ đúng khi được viết cùng nhau: một nhóm còn câu hỏi MỞ thì dòng của nó không được <c>[RÕ]</c>. Khi
/// chúng do hai lời gọi khác nhau chắt ra — bản đồ trong lượt chat, danh sách câu hỏi ở hậu kỳ — thì danh
/// sách luôn cũ hơn bản đồ đúng một lượt, và cổng "Write Requirement" bày ra một câu hỏi người dùng vừa
/// trả lời xong. Ghi chung một lượt thì độ trễ đó biến mất, và cùng với nó là cả tầng hoà giải hai bên.
/// </para>
/// <para>
/// <b><see cref="WorkedExamples"/> vào đây theo một lý lẽ KHÁC, và có giá của nó.</b> Nó không ràng buộc
/// hai chiều với bản đồ như danh sách câu hỏi; nó về đây để bỏ một lời gọi LLM chạy sau MỖI lượt chat mà
/// gần như luôn trả về mảng rỗng, và để <c>CoverageWorkedExampleGuard</c> đọc được bản của CHÍNH lượt này
/// thay vì bản cũ một lượt. Cái phải trả: guard ấy hạ dòng «Quy tắc nghiệp vụ» chở con số khi chưa có ví
/// dụ nào — nay cùng một lời gọi vừa viết dòng đó vừa viết cái bằng chứng miễn trừ nó, nên nó không còn
/// là một chốt chặn ĐỘC LẬP mà chỉ còn là một luật của prompt được cưỡng chế bằng code. Xem
/// <c>docs/requirement-flow.md</c>, mục "Ví dụ đã xác nhận về chung lượt distill".
/// </para>
/// </summary>
public class CoverageDistillDocument
{
    [Description("Đúng 12 nhóm thông tin, giữ nguyên thứ tự và tên nhóm của checklist.")]
    public List<CoverageMapEntry> Items { get; set; } = new();

    [Description("Mọi câu hỏi của cuộc phỏng vấn: mục còn phải hỏi (MỞ) và mục đã được trả lời (ĐÃ TRẢ LỜI). Một nhóm được phép có nhiều câu hỏi.")]
    public List<OpenQuestionEntry> Questions { get; set; } = new();

    /// <summary>
    /// <b>NULL ≠ mảng rỗng, và đó là cả lý do trường này nullable.</b> Mảng rỗng là một câu trả lời hợp lệ
    /// ("chưa ai chốt ví dụ nào", hoặc "ví dụ duy nhất vừa bị người dùng bác") nên nó GHI ĐÈ cột. Null là
    /// model không nói gì về danh sách — chuyện chỉ xảy ra ở đường parse tay, nhưng nếu tính nó là rỗng thì
    /// một lượt distill lơ đãng xoá trắng oracle mà POC bị chấm theo, và không ai thấy nó mất. Null ⇒ giữ
    /// nguyên cột đang lưu.
    /// <para>
    /// Khi lời gọi này còn là một prompt RIÊNG chỉ hỏi mỗi danh sách thì không có ca đó: trường vắng mặt
    /// tức lời gọi hỏng, và caller đã fail-open sẵn. Về chung một prompt 50KB lo mười hai dòng bản đồ thì
    /// một trường bị bỏ quên là chuyện phải tính tới.
    /// </para>
    /// </summary>
    [Description("Ví dụ ĐẦU VÀO CỤ THỂ → KẾT QUẢ KỲ VỌNG mà người dùng đã xác nhận. Mảng rỗng khi chưa chốt được ví dụ nào.")]
    public List<string>? WorkedExamples { get; set; }
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

    /// <summary>
    /// Danh sách chứ không phải một ô tóm tắt: xem <see cref="CoverageMapItem.Known"/> cho lý do, và
    /// <c>Prompts/BusinessAnalyst/requirement-coverage.v5.md</c> cho luật viết từng phần tử.
    /// <para>
    /// Đọc được cả bản đồ CŨ (trường này từng là một chuỗi) nhờ <c>CoverageKnownJsonConverter</c> — được
    /// đăng ký ở <c>CoverageMapParser</c> chứ không gắn <c>[JsonConverter]</c> lên đây, vì chính lớp này
    /// còn được đem đi sinh JSON schema cho structured output và một converter tự viết làm bộ sinh schema
    /// mất kiểu của trường.
    /// </para>
    /// </summary>
    [Description("Những điều đã biết về nhóm này, MỖI Ý MỘT PHẦN TỬ, bám sát lời người dùng. Chở TRẠNG THÁI MỚI NHẤT: người dùng đính chính thì sửa/xoá phần tử cũ. Rỗng khi CHƯA HỎI.")]
    public List<string> Known { get; set; } = new();
}
