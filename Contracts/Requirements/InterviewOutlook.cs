namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả chắt lọc "triển vọng phỏng vấn" từ hội thoại trong MỘT lời gọi (InterviewOutlookService):
///  • <see cref="OpenQuestions"/> — điểm còn MƠ HỒ / MÂU THUẪN chưa chốt: tồn đọng câu hỏi, nạp vào ngữ
///    cảnh lượt chat sau để BA hỏi cho hết ngay trong khung chat (KHÔNG có panel hiển thị).
///  • <see cref="ScopeAdditions"/> — phần PHẠM VI MỚI lộ ra ở các lượt vừa gộp: các màn hình / chức năng
///    chưa có trong bảng màn hình, đi thẳng vào bảng đó ở trạng thái CHỜ DUYỆT.
///  • <see cref="WorkedExamples"/> — các VÍ DỤ TÍNH THỬ người dùng ĐÃ XÁC NHẬN cho quy tắc định lượng,
///    nguồn để AI Design Spec đúc thành "## 13. Worked Examples" và POC tự kiểm đối chiếu độc lập.
/// Hai danh sách văn xuôi là câu ngắn (bullet), rỗng khi hội thoại chưa có gì tương ứng.
/// </summary>
public class InterviewOutlook
{
    public List<string> OpenQuestions { get; set; } = new();
    public List<ScopeAddition> ScopeAdditions { get; set; } = new();
    public List<string> WorkedExamples { get; set; } = new();
}

/// <summary>
/// MỘT mục phạm vi màn hình vừa lộ ra ở các lượt hội thoại mới — đơn vị mà lượt chắt lọc trả về để
/// <c>ScreenScopeMapBuilder.Merge</c> ghép vào bảng màn hình.
///
/// <para>
/// <b>Vì sao là DELTA chứ không phải cả danh sách.</b> Bản cũ bắt lượt chắt lọc viết lại TOÀN BỘ phạm vi
/// mỗi lượt (cột <c>Project.PlannedScope</c>), và cái giá không nằm ở token: chỉ cần model diễn đạt lại một
/// mục — <i>"…trong nhà máy"</i> thành <i>"…theo orgUnit"</i> — là danh sách đã khác, trong khi bảng người
/// dùng vừa rà thì không đổi. Mọi tầng sau phải sống chung với hai danh sách lệch chữ nhau: một phép so tập
/// hợp để đoán "màn hình mới", một đường ghi ngược sau lúc chốt, một danh sách cho phép phải đọc lại từ
/// lượt hội thoại vì cột kia đã bị viết đè giữa lúc bày bảng và lúc bấm gửi. Trả về ĐÚNG PHẦN MỚI thì không
/// có danh sách thứ hai để lệch: bảng màn hình là nguồn duy nhất, và lượt chắt lọc chỉ được phép THÊM vào.
/// </para>
///
/// <para>
/// Luật đặt tên <see cref="Screen"/> (tiếng Anh, 2–4 từ, danh từ chỉ nơi chốn) và luật "chức năng thì gộp
/// vào màn hình, không đứng thành mục riêng" sống trong prompt <c>interview-outlook.v1.md</c>; ở tầng bảng,
/// thứ dọn nốt phần lọt lưới là <see cref="ScreenScopeRow.Covers"/>.
/// </para>
/// </summary>
public class ScopeAddition
{
    /// <summary>
    /// Tên MÀN HÌNH mà mục này thuộc về — màn hình mới, hoặc một màn hình đã có trong bảng khi phần mới chỉ
    /// là <see cref="Functions"/> của nó.
    /// </summary>
    public string Screen { get; set; } = "";

    /// <summary>
    /// Màn hình này để làm gì, một câu. Chỉ dùng khi <see cref="Screen"/> là màn hình MỚI, và cũng chỉ là
    /// bản tạm: lượt BA bày bảng mới là chỗ ô "việc của màn" được điền cho tử tế.
    /// </summary>
    public string Purpose { get; set; } = "";

    /// <summary>
    /// Các CHỨC NĂNG mới trên màn hình này. Rỗng là hợp lệ (một màn hình vừa được nhắc tới mà chưa rõ trên
    /// đó làm gì); ngược lại, một màn hình ĐÃ CHỐT kèm chức năng mới ở đây là ca mà bản cũ không biểu diễn
    /// nổi — xem <see cref="ScreenFunction.ConfirmedByUser"/>.
    /// </summary>
    public List<string> Functions { get; set; } = new();
}
