namespace ICOGenerator.Services.Requirements;

/// <summary>
/// GIAO KÈO của một lượt bày bảng: lượt này đòi model trả về trường JSON nào, gọi cái bảng ấy là gì khi
/// nói với người dùng, và đòi lại bằng câu gì khi model không trả.
///
/// <para>
/// <b>Vì sao là một bảng tra cứu chứ không phải vài chuỗi rải trong <see cref="BAChatService"/>.</b> Ba
/// mẩu này luôn đi cùng nhau và luôn phải khớp nhau: dòng trạng thái nói *"đang dựng bảng đối tượng"* thì
/// lời đòi lại phải đòi đúng <c>entityMap</c>, không phải một trường khác. Rải ra thì lần thêm bảng thứ
/// bảy chỉ sửa hai trong ba chỗ, và cái sai lộ ra ở đúng lượt hiếm nhất — lượt model trả thiếu.
/// </para>
///
/// <para>
/// <b>Tên trường ở đây phải khớp <c>BAChatReply</c> và khớp prompt của bảng.</b> Nó được đọc ra cho model
/// nghe, nên một tên lệch biến lời đòi lại thành lời đòi một trường không tồn tại — model sẽ trả lại đúng
/// thứ nó vừa trả, và cả bốn lượt gọi đều trượt.
/// </para>
/// </summary>
public sealed record InterviewTableContract(string Field, string Label, string Noun)
{
    /// <summary>
    /// Lời ĐÒI LẠI gửi kèm ở lượt gọi sau, dưới vai người dùng, ngay sau bản trả lời hỏng của model.
    ///
    /// <para>
    /// Nó nói đúng ba việc và không nói gì thêm: lượt vừa rồi hỏng ở đâu, phải trả lại cái gì, và tuyệt đối
    /// không được đổi câu dẫn thành câu hỏi để "gỡ" — vì đúng cái đó là hình dạng mà chốt chặn lượt câm sẽ
    /// bắt. Không nhắc lại đặc tả trường: khối <c>## LƯỢT NÀY:</c> vẫn còn nguyên ở đầu ngữ cảnh, chép lại
    /// nó ở đây là dựng thêm một bản thứ hai để lần sửa sau bỏ quên.
    /// </para>
    /// </summary>
    public string RetryDemand =>
        $"Câu trả lời vừa rồi KHÔNG dùng được: nó thiếu trường `{Field}` (hoặc trường đó rỗng, hoặc mọi dòng "
        + $"trong đó đều không hợp lệ), nên {Noun} không hiện ra được và người dùng không có gì để rà.\n"
        + $"Trả lại TOÀN BỘ JSON của lượt này, lần này BẮT BUỘC có `{Field}` với ít nhất một dòng, theo đúng "
        + $"đặc tả ở khối \"## LƯỢT NÀY:\" phía trên.\n"
        + "Giữ nguyên hình dạng lượt: `message` vẫn là MỘT câu ngắn mời rà bảng rồi bấm nút, `suggestions` "
        + "và `questions` vẫn rỗng. ĐỪNG thay bảng bằng một câu hỏi hay một lời hứa sẽ làm ở lượt sau — "
        + "lượt này không có chỗ nào khác để người dùng trả lời ngoài bảng.";

    private static readonly IReadOnlyDictionary<InterviewTableKind, InterviewTableContract> ByKind =
        new Dictionary<InterviewTableKind, InterviewTableContract>
        {
            [InterviewTableKind.FlowMap] = new("flowMap", "bảng luồng nghiệp vụ", "bảng luồng"),
            [InterviewTableKind.ScreenScope] = new("screenScopeMap", "bảng màn hình", "bảng màn hình"),
            [InterviewTableKind.EntityMap] = new("entityMap", "bảng đối tượng nghiệp vụ", "bảng đối tượng"),
            [InterviewTableKind.ReportMap] = new("reportMap", "bảng báo cáo", "bảng báo cáo"),
            [InterviewTableKind.PermissionMatrix] = new("permissionMatrix", "bảng phân quyền", "bảng phân quyền"),
            [InterviewTableKind.NotificationMap] = new("notificationMap", "bảng thông báo", "bảng thông báo")
        };

    /// <summary>
    /// Giao kèo của một cổng bảng, hoặc <c>null</c> cho <see cref="InterviewTableKind.None"/> — lượt chat
    /// thường không đòi trường nào nên cũng không có gì để đòi lại.
    /// </summary>
    public static InterviewTableContract? For(InterviewTableKind kind)
        => ByKind.TryGetValue(kind, out var contract) ? contract : null;
}
