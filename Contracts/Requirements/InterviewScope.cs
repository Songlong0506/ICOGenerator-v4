namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả của lượt chắt lọc PHẠM VI MÀN HÌNH (<c>InterviewScopeService</c>): các màn hình / chức năng vừa
/// lộ ra trong hội thoại mà bảng màn hình chưa có, ghép thẳng vào bảng đó ở trạng thái CHỜ DUYỆT.
///
/// <para>
/// <b>Vì sao nó là một lời gọi RIÊNG chứ không còn là danh sách thứ ba của
/// <see cref="InterviewOutlook"/>.</b> Hai danh sách kia là ảnh chụp trạng thái phục vụ chính lượt chat kế
/// tiếp (tồn đọng câu hỏi nạp vào ngữ cảnh của BA), nên chúng phải tươi sau MỖI lượt. Phạm vi màn hình thì
/// không: nó chỉ được tiêu thụ khi bảng màn hình được bày ra hỏi — một hai lần trong cả buổi. Chở nó theo
/// mỗi lượt là trả giá hai lần. Lần thứ nhất bằng token: luật đặt tên màn hình cộng luật "chỉ màn hình,
/// chức năng thì gộp vào màn chứa nó" chiếm hơn một phần ba prompt, và khối "bảng màn hình đang có" phải
/// kể tới từng chức năng để model biết cái gì đã có. Lần thứ hai đắt hơn, bằng chất lượng: ở những lượt
/// đầu buổi thì bảng luồng chưa chốt, bảng đối tượng chưa có, phạm vi chưa hình thành — mọi màn hình model
/// đoán ra lúc ấy là phỏng đoán sớm, mà <c>ScreenScopeMapBuilder.Merge</c> thì chỉ THÊM chứ không bao giờ
/// bớt. Một dòng sai ở lượt 3 nằm lại trong bảng cho tới khi chính người dùng bỏ tích nó ở lượt 25.
/// </para>
///
/// <para>
/// Nhịp mới ở <c>InterviewScopeService.ShouldHarvest</c>: im lặng cho tới khi buổi phỏng vấn đi tới sát
/// cổng bảng màn hình, rồi gộp bù cả quãng đã qua trong MỘT lời gọi; sau lần chốt đầu thì chạy theo LÔ để
/// vẫn bắt được phần phạm vi trôi tiếp.
/// </para>
/// </summary>
public class InterviewScope
{
    public List<ScopeAddition> ScopeAdditions { get; set; } = new();
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
/// vào màn hình, không đứng thành mục riêng" sống trong prompt <c>interview-scope.v1.md</c>; ở tầng bảng,
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
