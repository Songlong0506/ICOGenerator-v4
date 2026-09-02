using System.ComponentModel;

namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Hình dạng JSON của "Điểm cần làm rõ còn tồn đọng" — vừa là schema cho structured output của lượt
/// chắt lọc (<c>InterviewOutlookService</c>), vừa là format LƯU TRỮ trên <c>Project.OpenQuestions</c>.
/// Cùng pattern với <see cref="CoverageMapDocument"/>, và cùng một lý do.
/// <para>
/// <b>Vì sao là JSON.</b> Danh sách này từng là các dòng bullet, mỗi dòng nhồi HAI trường vào một chuỗi:
/// <c>- [Vòng đời &amp; trạng thái] Chưa rõ kết quả Complete dùng để chuyển bước nào</c>. Thẻ nhóm ở đầu
/// không phải chữ trang trí — <c>CoveragePendingGuard</c> đối chiếu nó với nhãn dòng bản đồ bao phủ để
/// hạ một dòng <c>[RÕ]</c> oan xuống <c>[MỘT PHẦN]</c>, tức nó là đầu vào của một chốt chặn tất định.
/// Nhưng nó chỉ tồn tại nhờ prompt DẶN model gõ đúng khuôn <c>[…]</c> ở đầu chuỗi, và ba chỗ đọc đều
/// phải regex bóc lại: một cái để lấy cặp nhóm/câu hỏi, hai cái chỉ để VỨT thẻ đi trước khi nạp vào ngữ
/// cảnh. Model gõ chệch khuôn ⇒ regex không khớp ⇒ guard câm trong im lặng, và cái giá của im lặng ở đây
/// là <c>[RÕ]</c> — lệnh cấm BA hỏi lại, nên điểm tồn đọng ấy vĩnh viễn không được lấy.
/// </para>
/// <para>
/// Trường bậc nhất đổi chỗ ràng buộc đó: model điền một TRƯỜNG của schema thay vì tự dựng cú pháp trong
/// chuỗi, và <c>InterviewOutlookService</c> snap <see cref="OpenQuestionEntry.Group"/> về đúng một trong
/// 12 nhãn của checklist NGAY Ở ĐƯỜNG GHI — nên mọi tầng đọc sau đó chỉ còn đọc thuộc tính.
/// </para>
/// <para>
/// Tên thuộc tính để ASCII có chủ đích, cùng lý do với <see cref="CoverageMapDocument"/>: schema này được
/// gửi thẳng cho model qua <c>response_format: json_schema</c>.
/// </para>
/// </summary>
public class OpenQuestionDocument
{
    [Description("Các điểm còn mơ hồ/mâu thuẫn chưa chốt. Không còn điểm nào ⇒ mảng rỗng.")]
    public List<OpenQuestionEntry> Items { get; set; } = new();
}

/// <summary>Một điểm còn phải làm rõ trong <see cref="OpenQuestionDocument"/>.</summary>
public class OpenQuestionEntry
{
    [Description("Chép ĐÚNG MỘT nhãn nhóm của bản đồ bao phủ mà điểm này thuộc về. Không thuộc nhóm nào thì để rỗng.")]
    public string Group { get; set; } = string.Empty;

    [Description("Câu ngắn nêu RÕ điều còn thiếu, đúng ngôn ngữ người dùng. KHÔNG chép tên nhóm vào đây.")]
    public string Text { get; set; } = string.Empty;
}
