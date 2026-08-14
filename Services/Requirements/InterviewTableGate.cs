using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>Bảng mà một lượt chat được phép bày ra. <see cref="None"/> = lượt chat thường.</summary>
public enum InterviewTableKind
{
    /// <summary>Không bảng nào — lượt hỏi/trả lời bình thường.</summary>
    None,

    /// <summary>Bảng LUỒNG nghiệp vụ theo vai trò (luồng chính + ngoại lệ).</summary>
    FlowMap,

    /// <summary>Bảng MÀN HÌNH dự kiến, kèm bước luồng mà mỗi màn phục vụ.</summary>
    ScreenScope,

    /// <summary>Bảng ĐỐI TƯỢNG nghiệp vụ: thông tin cần lưu + vòng đời trạng thái + ai được báo.</summary>
    EntityMap,

    /// <summary>Bảng PHÂN QUYỀN (màn hình × chức năng × vai trò) — cổng cuối cùng.</summary>
    PermissionMatrix
}

/// <summary>
/// Chọn ĐÚNG MỘT bảng cho một lượt chat, tất định, từ chính bản đồ bao phủ và các bảng đã chốt.
///
/// <para>
/// <b>Vì sao phải có một chỗ chọn duy nhất.</b> Mỗi cổng bơm một khối <c>## LƯỢT NÀY:</c> vào ngữ cảnh, và
/// hai khối như thế cùng lúc là hai mệnh lệnh chọi nhau — model sẽ trả một bảng lai hoặc bỏ cả hai. Repo đã
/// gặp đúng chuyện này ở quy mô nhỏ hơn: cổng bảng phân quyền phải NHƯỜNG một lượt cho lượt kể lại file
/// bảng tính vì cùng lý do. Với bốn bảng thì việc nhường không còn là một ngoại lệ viết tay được nữa, nên
/// nó thành một hàm.
/// </para>
///
/// <para>
/// <b>Thứ tự ưu tiên là thứ tự PHỤ THUỘC, không phải thứ tự tiện tay.</b> Luồng trước, vì màn hình được
/// suy ra từ bước luồng và bảng màn hình có một ô hỏi thẳng "màn này phục vụ bước nào". Màn hình trước đối
/// tượng, vì cái người dùng nhìn thấy trên màn hình quyết định thông tin nào thật sự cần lưu. Phân quyền
/// cuối cùng, vì các DÒNG của nó là màn hình — hỏi trước khi phạm vi màn hình đứng yên thì bảng thiếu nửa
/// số dòng, mà quyền của một màn hình chưa tồn tại thì không ai trả lời được.
/// </para>
///
/// <para>
/// <b>Vì sao ba bảng mới KHÔNG được là điều kiện để một nhóm lên <c>[RÕ]</c>.</b> Nhóm «Phân quyền theo
/// nghiệp vụ» có luật khắt khe một chiều — chưa có bảng thì không bao giờ <c>[RÕ]</c> — và luật đó đúng vì
/// nhóm ấy KHÔNG được hỏi bằng câu hỏi. Ba nhóm còn lại thì có: chúng được hỏi suốt buổi, và các bảng dưới
/// đây chỉ XÁC NHẬN LẠI thứ hội thoại đã trả lời. Áp luật một chiều cho chúng là dựng một vòng khóa kín —
/// cổng đòi nhóm <c>[RÕ]</c> mới mở, bản đồ đòi có bảng mới <c>[RÕ]</c>, và không bên nào đi trước được.
/// Đó chính là cái bẫy mà <see cref="PermissionMatrixGate"/> đã phải né bằng cách cố ý bỏ qua đúng dòng
/// phân quyền khi xét, và nó chỉ né được vì lúc đó chỉ có MỘT bảng.
/// </para>
///
/// <para>
/// Hệ quả: khi <see cref="PermissionMatrixGate"/> mở (mọi nhóm áp dụng khác đã <c>[RÕ]</c>), điều kiện của
/// cả ba cổng kia đương nhiên cũng đã đúng — nên bảng nào chưa chốt sẽ lần lượt được hỏi TRƯỚC nó, và
/// không cổng nào cần biết cổng khác tồn tại. Cổng cuối cùng vẫn là thứ mở nút "Write Requirement", nên
/// không có đường nào soạn tài liệu mà bỏ qua các bảng này.
/// </para>
/// </summary>
public static class InterviewTableGate
{
    /// <summary>
    /// Bảng của lượt này. <paramref name="suppressed"/> = lượt đã có việc riêng và chỉ có MỘT chỗ trả lời
    /// (lượt kể lại file bảng tính) ⇒ mọi cổng nhường một lượt, chúng mở lại ngay lượt sau.
    /// </summary>
    public static InterviewTableKind Select(Project project, bool suppressed = false)
    {
        if (suppressed)
            return InterviewTableKind.None;

        if (FlowMapGate.ShouldAsk(project))
            return InterviewTableKind.FlowMap;
        if (ScreenScopeGate.ShouldAsk(project))
            return InterviewTableKind.ScreenScope;
        if (EntityMapGate.ShouldAsk(project))
            return InterviewTableKind.EntityMap;
        if (PermissionMatrixGate.ShouldAsk(project))
            return InterviewTableKind.PermissionMatrix;

        return InterviewTableKind.None;
    }

    /// <summary>Nhãn nhóm trong bản đồ bao phủ, khớp <c>requirement-coverage.v3.md</c>.</summary>
    internal static class Groups
    {
        public const string Roles = "Đối tượng người dùng & vai trò";
        public const string MainFlow = "Chức năng & luồng nghiệp vụ chính";
        public const string ExceptionFlow = "Luồng ngoại lệ";
        public const string Data = "Dữ liệu / danh mục chính";
        public const string Lifecycle = "Vòng đời & trạng thái";
        public const string Notification = "Thông báo / nhắc nhở";
    }

    /// <summary>
    /// Nhóm mang nhãn bắt đầu bằng <paramref name="prefix"/> đã ở trạng thái <c>[RÕ]</c> chưa. So khớp bằng
    /// TIỀN TỐ vì cùng lý do với <see cref="PermissionMatrixGate.PermissionGroupLabel"/>: một lượt distill
    /// viết chệch phần đuôi nhãn không được phép làm cổng câm vĩnh viễn. Nhóm không có mặt trong bản đồ ⇒
    /// false (fail-closed): bản đồ thiếu dòng là bản đồ hỏng, không phải một nhóm đã xong.
    /// </summary>
    internal static bool IsClear(IReadOnlyList<CoverageMapItem> items, string prefix)
        => Find(items, prefix)?.Status == "RÕ";

    /// <summary>
    /// Nhóm đã được CHẠM TỚI: <c>[RÕ]</c> hoặc <c>[KHÔNG ÁP DỤNG]</c>. Dùng cho các nhóm mà bảng chỉ chở
    /// một phần (ngoại lệ, thông báo) — đòi <c>[RÕ]</c> ở đó là hoãn cổng tới tận lúc cổng phân quyền mở,
    /// tức dồn cả bốn bảng vào cuối buổi, đúng thứ thiết kế này muốn tránh.
    /// </summary>
    internal static bool IsSettled(IReadOnlyList<CoverageMapItem> items, string prefix)
    {
        var status = Find(items, prefix)?.Status;
        return status is "RÕ" or "KHÔNG ÁP DỤNG";
    }

    private static CoverageMapItem? Find(IReadOnlyList<CoverageMapItem> items, string prefix)
        => items.FirstOrDefault(x => (x.Label ?? string.Empty).Trim()
            .StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Cổng TẤT ĐỊNH cho BẢNG LUỒNG NGHIỆP VỤ — bảng đầu tiên của chuỗi, mở ở GIỮA buổi phỏng vấn.
///
/// <para>
/// Điều kiện mở, cả ba đều bắt buộc:
/// </para>
/// <list type="number">
///   <item><b>Chưa chốt bảng nào</b> — chốt rồi thì các luồng đã có bản người dùng tự tay duyệt.</item>
///   <item><b>«Chức năng &amp; luồng nghiệp vụ chính» và «Đối tượng người dùng &amp; vai trò» đã
///   <c>[RÕ]</c></b> — không biết ai làm gì thì bảng luồng chỉ là bản BA đoán, và một bảng đoán thì người
///   dùng đọc lướt rồi gật.</item>
///   <item><b>«Luồng ngoại lệ» đã được CHẠM TỚI</b> (<c>[RÕ]</c> hoặc <c>[KHÔNG ÁP DỤNG]</c>) — bảng có
///   phần ngoại lệ, và bày nó ra khi chưa ai hỏi tới đường hỏng nào là mời model bịa ra một ngoại lệ để
///   lấp chỗ trống. Không đòi <c>[MỘT PHẦN]</c> lên <c>[RÕ]</c> ở nhóm này: chuẩn <c>[RÕ]</c> của nó khắt
///   khe (một tình huống hỏng cụ thể kèm cách xử lý), và bảng chính là chỗ rẻ nhất để lấy nốt phần
///   thiếu.</item>
/// </list>
/// </summary>
public static class FlowMapGate
{
    /// <summary>Đã tới lúc bày bảng luồng cho dự án này chưa.</summary>
    public static bool ShouldAsk(Project project)
        => ShouldAsk(project.RequirementCoverageMap, project.FlowMap);

    /// <summary>Bản thuần dữ liệu — để test và để gọi từ nơi không có entity.</summary>
    public static bool ShouldAsk(string? coverageMap, string? flowMapJson)
    {
        if (FlowMapBuilder.IsConfirmed(flowMapJson))
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        return InterviewTableGate.IsClear(items, InterviewTableGate.Groups.MainFlow)
               && InterviewTableGate.IsClear(items, InterviewTableGate.Groups.Roles)
               && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.ExceptionFlow);
    }
}

/// <summary>
/// Cổng TẤT ĐỊNH cho BẢNG MÀN HÌNH — bảng thứ hai, mở ngay sau khi luồng đã chốt.
///
/// <para>
/// Hai lượt bảng liền nhau ở đây là CỐ Ý, không phải sơ suất về nhịp: người dùng vừa gật một chuỗi bước,
/// và câu tiếp theo tự nhiên nhất là "các bước đó sẽ thành những màn hình sau". Đặt cách xa nhau thì bảng
/// màn hình mất luôn ngữ cảnh khiến ô "phục vụ bước nào" đọc được.
/// </para>
///
/// <para>
/// Điều kiện mở:
/// </para>
/// <list type="number">
///   <item><b>Chưa chốt bảng — HOẶC đã chốt mà có màn hình MỚI lộ ra sau đó.</b> Xem mục dưới.</item>
///   <item><b>Phạm vi đã có mục</b> (<c>Project.PlannedScope</c>) — các DÒNG của bảng chính là nó.</item>
///   <item><b>«Chức năng &amp; luồng nghiệp vụ chính» đã <c>[RÕ]</c>.</b></item>
/// </list>
///
/// <para>
/// KHÔNG đòi bảng luồng phải chốt trước. Model có thể không trả nổi một bảng luồng dùng được (structured
/// output tắt, hoặc mọi luồng đều một bước) — trói cổng này vào đó là để một lượt hỏng chặn vĩnh viễn cả
/// phần còn lại của chuỗi. Thứ tự vẫn được giữ ở <see cref="InterviewTableGate.Select"/>, nơi cổng luồng
/// được xét trước; ở đây fail-open là lựa chọn đúng.
/// </para>
///
/// <para>
/// <b>Cổng DUY NHẤT trong bốn cổng mở lại được sau khi đã chốt</b> — vì nó là cổng duy nhất mà phạm vi có
/// thể trôi tiếp sau lượt chốt. Ca thật (dự án Learning and Development 7): bảng chốt ở lượt 23; tới lượt
/// 33 người dùng nói sĩ số tối thiểu/tối đa lấy từ *"danh sách khóa học được quản lý ở một màn hình
/// riêng"*, và Admin đã được chốt là người quản lý cả phòng học lẫn người dạy. Ba màn hình đó có mặt trong
/// <c>PlannedScope</c> nhưng không bao giờ đi qua bảng: <see cref="ScreenScopeMapBuilder.EffectiveScreens"/>
/// bù chúng vào bảng phân quyền ở dạng TRẮNG (không việc, không chức năng, không bước luồng), trong khi
/// khối ngữ cảnh của bảng đã chốt CẤM BA hỏi lại việc của từng màn — nên chúng đi vào tài liệu và vào bản
/// demo mà không ai biết chúng để làm gì.
/// </para>
///
/// <para>
/// Mở lại KHÔNG phải rà lại từ đầu: lượt bày lại được gieo bằng
/// <see cref="ScreenScopeMapBuilder.SeedRows"/> nên các dòng người dùng đã duyệt giữ nguyên việc, chức
/// năng và ô "phục vụ bước nào"; phần tươi chỉ là các màn hình mới. Vòng lặp có đáy: người dùng giữ màn
/// hình mới ⇒ nó thành một dòng của bảng ⇒ hết "mới"; bỏ tích ⇒ <c>ConfirmScreenScopeUseCase</c> ghi ngược
/// <c>PlannedScope</c> nên nó rời phạm vi ⇒ cũng hết "mới". Cả hai đường đều đóng cổng.
/// </para>
/// </summary>
public static class ScreenScopeGate
{
    /// <summary>Đã tới lúc bày (hoặc bày LẠI) bảng màn hình cho dự án này chưa.</summary>
    public static bool ShouldAsk(Project project)
        => ShouldAsk(project.RequirementCoverageMap, project.ScreenScopeMap,
            InterviewOutlookService.ParseItems(project.PlannedScope));

    /// <summary>Bản thuần dữ liệu — để test và để gọi từ nơi không có entity.</summary>
    public static bool ShouldAsk(string? coverageMap, string? screenScopeJson, IReadOnlyList<string> plannedScope)
    {
        if (plannedScope.Count == 0)
            return false;

        // Đã chốt ⇒ chỉ mở lại khi có màn hình MỚI lộ ra sau lúc chốt. Không có mục mới nào thì bảng đã là
        // câu trả lời của người dùng, và bày lại một bảng y hệt là bắt họ làm lại việc vừa làm.
        if (ScreenScopeMapBuilder.IsConfirmed(screenScopeJson)
            && ScreenScopeMapBuilder.NewScreens(screenScopeJson, plannedScope).Count == 0)
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        return InterviewTableGate.IsClear(items, InterviewTableGate.Groups.MainFlow);
    }
}

/// <summary>
/// Cổng TẤT ĐỊNH cho BẢNG ĐỐI TƯỢNG NGHIỆP VỤ — bảng thứ ba.
///
/// <para>
/// Điều kiện mở:
/// </para>
/// <list type="number">
///   <item><b>Chưa chốt bảng nào.</b></item>
///   <item><b>«Dữ liệu / danh mục chính» đã <c>[RÕ]</c></b> — đây là nhóm chở phần lớn nội dung bảng.</item>
///   <item><b>«Vòng đời &amp; trạng thái» và «Thông báo / nhắc nhở» đã được CHẠM TỚI</b> — hai cột của
///   bảng. Chỉ đòi chạm tới chứ không đòi <c>[RÕ]</c>: chuẩn <c>[RÕ]</c> của chúng khắt khe (gọi tên trạng
///   thái + điều kiện chuyển; ai nhận + khi nào, hai vế phải ghép được với nhau) và bảng chính là chỗ rẻ
///   nhất để lấy nốt phần thiếu — bắt hội thoại làm xong việc đó trước rồi mới bày bảng là bỏ đúng lý do
///   bảng tồn tại.</item>
/// </list>
/// </summary>
public static class EntityMapGate
{
    /// <summary>Đã tới lúc bày bảng đối tượng cho dự án này chưa.</summary>
    public static bool ShouldAsk(Project project)
        => ShouldAsk(project.RequirementCoverageMap, project.EntityMap);

    /// <summary>Bản thuần dữ liệu — để test và để gọi từ nơi không có entity.</summary>
    public static bool ShouldAsk(string? coverageMap, string? entityMapJson)
    {
        if (EntityMapBuilder.IsConfirmed(entityMapJson))
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        return InterviewTableGate.IsClear(items, InterviewTableGate.Groups.Data)
               && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.Lifecycle)
               && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.Notification);
    }
}
