using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Localization;
namespace ICOGenerator.Services.Requirements;

/// <summary>
/// CÂU MỞ ĐẦU của từng nhóm trong bản đồ bao phủ — câu mà <see cref="RequirementReadinessGate"/> dùng khi
/// nó phải tự đặt câu hỏi thay BA và bản đồ không cho nó mẩu <c>còn thiếu:</c> nào để bám.
///
/// <para>
/// <b>Vì sao phải có bảng này.</b> Trước đây nhánh dự phòng của cổng phát MỘT câu duy nhất cho cả 12 nhóm:
/// *"Anh/chị kể giúp mình phần này trong công việc thực tế hiện đang diễn ra thế nào?"*. Câu đó không nói
/// được nhóm nào, không nói được đang hỏi cái gì, và trỏ tới *"phần này"* — đúng cụm THAM CHIẾU SUÔNG mà
/// <c>requirement-chat.v4.md</c> cấm BA dùng, vì người dùng chỉ thấy ô chat cuối trên màn hình. Ca thật ở
/// dự án JD Library, lượt 76: người dùng vừa trả lời xong một sự kiện thông báo thì nhận đúng câu đó và đáp
/// *"mình chưa hiểu câu hỏi, hãy hỏi rõ hơn"* — mất trắng một vòng ở cuối buổi phỏng vấn thứ 78. Cùng hình
/// dạng với vòng lặp câu hỏi chết mà <c>CoverageDeadQuestionLoopTests</c> đã chốt: nhánh này KHÔNG được
/// phép là một câu trống nghĩa, vì nó là lượt duy nhất người dùng nhìn thấy khi cổng chặn.
/// </para>
///
/// <para>
/// <b>Luật viết câu.</b> Hỏi bằng ngôn ngữ công việc của người dùng nghiệp vụ, KHÔNG dùng từ vựng nội bộ
/// của bản đồ ("vòng đời", "phạm vi", "đặc tả", "danh mục") ngay trong câu hỏi; kết thúc bằng dấu hỏi; và
/// hỏi ĐÚNG MỘT thứ — lượt chặn của cổng không có chip nào nên nó là câu MỞ, mà một câu mở nhiều vế thì
/// người dùng chỉ trả lời được vế cuối. Câu ở đây phải ĐỨNG MỘT MÌNH ĐƯỢC: cổng phát nó nguyên si, không
/// kèm câu dẫn nào nêu nhóm — nhãn nhóm không bao giờ lên màn hình nữa (xem
/// <see cref="RequirementReadinessGate"/>), nên một câu chỉ có nghĩa khi biết nó thuộc nhóm nào là một câu
/// hỏng. Ai muốn xem nhãn thì panel "Tiến độ khai thác" bên cạnh vẫn liệt kê đủ.
/// </para>
///
/// <para>
/// <b>Hai nhóm đọc khác hẳn</b> — «Thông báo / nhắc nhở» và «Phân quyền theo nghiệp vụ» là hai nhóm bị
/// prompt CẤM hỏi bằng câu hỏi (chúng được chốt bằng BẢNG ở cuối buổi, xem <see cref="InterviewTableGate"/>).
/// Phát cho chúng một câu hỏi khai thác là cổng tự phá đúng chốt chặn mà hai cái bảng sinh ra để dựng: một
/// danh sách vai trò trần trả lời cho cả nhóm. Vì vậy câu của hai nhóm này chỉ MỜI người dùng nhắn một
/// tiếng để phần đó được đưa ra rà — và cố tình không hứa hẹn "một cái bảng", vì dự án không có vòng đời
/// trạng thái nào thì bảng thông báo KHÔNG bao giờ được bày và nhóm quay về đường hỏi bằng câu hỏi
/// (<see cref="NotificationMapGate"/>). Cổng chỉ có bản đồ bao phủ trong tay nên nó không phân biệt được
/// hai ca đó; câu chữ vì thế phải đúng ở cả hai.
/// </para>
/// </summary>
public static class CoverageGroupOpeners
{
    // Câu của từng nhóm nằm ở resource `CoverageGroup.Opener.<khoá>` — mỗi ngôn ngữ một bản, nên câu mở đầu
    // đi theo ngôn ngữ của buổi phỏng vấn. CoverageGroupOpenersTests fail build nếu một khoá của
    // CoverageGroupKey.All không có câu — thiếu một câu ở đây là nhánh dự phòng lại rơi về một lượt trống
    // nghĩa.
    //
    // Ghi chú cho người viết / dịch các câu đó:
    //  - «notifications» và «permissions» là hai nhóm chốt bằng BẢNG — xem phần "Hai nhóm đọc khác hẳn" ở
    //    doc của class.
    //  - «reports» KHÔNG hỏi kèm "ai là người xem": báo cáo đứng trên một MÀN HÌNH (xem ReportMapBuilder),
    //    nên quyền xem của nó thuộc bảng phân quyền — hỏi ở đây là bắt người dùng trả lời hai lần cho cùng một
    //    điều, và nó cũng phá luật "hỏi ĐÚNG MỘT thứ" (câu mở nhiều vế thì chỉ vế cuối được trả lời).
    //  - «scale»: vế người dùng hỏi bằng TÊN nhóm người, KHÔNG bằng bậc thang đầu người ("Trên 1000 nhân
    //    viên"): tổng số nhân sự internal và số nhân sự từng department đã nằm trong khối "Bối cảnh tổ chức
    //    Bosch" đính kèm mọi lời gọi BA, nên hỏi con số là hỏi đúng thứ hệ thống đang cầm — bằng thứ ngôn
    //    ngữ người dùng không dùng (xem organization-scope.v1.md). Vế khối lượng thì ngược lại: không ngữ
    //    cảnh nào trả lời hộ được, nên nó vẫn hỏi bằng con số.

    /// <summary>
    /// Câu mở đầu của nhóm mang khoá <paramref name="key"/> (<see cref="CoverageGroupKey"/>), hoặc
    /// <c>null</c> khi khoá không thuộc checklist (dòng bản đồ model tự nghĩ ra một tên nhóm). Caller phải
    /// chịu được <c>null</c>: dựng một câu hỏi bịa cho một nhóm không tồn tại còn tệ hơn một câu dẫn chung,
    /// vì nó hỏi người dùng về một thứ không có trong checklist nào.
    /// </summary>
    public static string? Find(string? key)
        => key != null && CoverageGroupKey.All.Contains(key) ? Loc.T("CoverageGroup.Opener." + key) : null;
}
