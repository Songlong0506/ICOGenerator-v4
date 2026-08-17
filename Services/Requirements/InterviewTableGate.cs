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

    /// <summary>Bảng ĐỐI TƯỢNG nghiệp vụ: thông tin cần lưu + vòng đời trạng thái.</summary>
    EntityMap,

    /// <summary>Bảng PHÂN QUYỀN (màn hình × chức năng × vai trò).</summary>
    PermissionMatrix,

    /// <summary>Bảng THÔNG BÁO / NHẮC NHỞ (sự kiện × To × CC) — cổng cuối cùng.</summary>
    NotificationMap
}

/// <summary>
/// Chọn ĐÚNG MỘT bảng cho một lượt chat, tất định, từ chính bản đồ bao phủ và các bảng đã chốt.
///
/// <para>
/// <b>Vì sao phải có một chỗ chọn duy nhất.</b> Mỗi cổng bơm một khối <c>## LƯỢT NÀY:</c> vào ngữ cảnh, và
/// hai khối như thế cùng lúc là hai mệnh lệnh chọi nhau — model sẽ trả một bảng lai hoặc bỏ cả hai. Repo đã
/// gặp đúng chuyện này ở quy mô nhỏ hơn: cổng bảng phân quyền phải NHƯỜNG một lượt cho lượt kể lại file
/// bảng tính vì cùng lý do. Với năm bảng thì việc nhường không còn là một ngoại lệ viết tay được nữa, nên
/// nó thành một hàm.
/// </para>
///
/// <para>
/// <b>Thứ tự ưu tiên là thứ tự PHỤ THUỘC, không phải thứ tự tiện tay.</b> Luồng trước, vì màn hình được
/// suy ra từ bước luồng và bảng màn hình có một ô hỏi thẳng "màn này phục vụ bước nào". Màn hình trước đối
/// tượng, vì cái người dùng nhìn thấy trên màn hình quyết định thông tin nào thật sự cần lưu. Phân quyền
/// gần cuối, vì các DÒNG của nó là màn hình — hỏi trước khi phạm vi màn hình đứng yên thì bảng thiếu nửa
/// số dòng, mà quyền của một màn hình chưa tồn tại thì không ai trả lời được. Thông báo CUỐI CÙNG, vì nó
/// vay cả hai chiều: các DÒNG là chuyển trạng thái của bảng đối tượng, còn danh sách người nhận cần các
/// VAI TRÒ của bảng phân quyền — vai trò của ứng dụng đang thiết kế chỉ tồn tại trong hội thoại, không có
/// bảng nào trong DB liệt kê chúng.
/// </para>
///
/// <para>
/// <b>Vì sao ba bảng GIỮA không được là điều kiện để một nhóm lên <c>[RÕ]</c>.</b> Hai nhóm cuối («Phân
/// quyền theo nghiệp vụ» và «Thông báo / nhắc nhở») có luật khắt khe một chiều — chưa có bảng thì không
/// bao giờ <c>[RÕ]</c> — và luật đó đúng vì cả hai KHÔNG được hỏi bằng câu hỏi. Ba nhóm của các bảng giữa
/// thì có: chúng được hỏi suốt buổi, và bảng chỉ XÁC NHẬN LẠI thứ hội thoại đã trả lời. Áp luật một chiều
/// cho chúng là dựng một vòng khóa kín — cổng đòi nhóm <c>[RÕ]</c> mới mở, bản đồ đòi có bảng mới
/// <c>[RÕ]</c>, và không bên nào đi trước được. Đó chính là cái bẫy mà <see cref="PermissionMatrixGate"/>
/// né bằng cách cố ý BỎ QUA hai dòng đó khi xét.
/// </para>
///
/// <para>
/// Hệ quả: khi <see cref="PermissionMatrixGate"/> mở (mọi nhóm áp dụng khác đã <c>[RÕ]</c>), điều kiện của
/// cả ba cổng kia đương nhiên cũng đã đúng — nên bảng nào chưa chốt sẽ lần lượt được hỏi TRƯỚC nó, và
/// không cổng nào cần biết cổng khác tồn tại. Bảng phân quyền rồi bảng thông báo là hai bảng cuối, và
/// nhóm của cả hai chỉ <c>[RÕ]</c> sau khi bảng được chốt, nên không có đường nào soạn tài liệu mà bỏ qua
/// các bảng này.
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
        if (NotificationMapGate.ShouldAsk(project))
            return InterviewTableKind.NotificationMap;

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
    /// một phần (ngoại lệ, vòng đời) — đòi <c>[RÕ]</c> ở đó là hoãn cổng tới tận lúc cổng phân quyền mở,
    /// tức dồn mọi bảng vào cuối buổi, đúng thứ thiết kế này muốn tránh.
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
///   <item><b>«Vòng đời &amp; trạng thái» đã được CHẠM TỚI</b> — cột trạng thái của bảng. Chỉ đòi chạm tới
///   chứ không đòi <c>[RÕ]</c>: chuẩn <c>[RÕ]</c> của nhóm này khắt khe (gọi tên trạng thái + điều kiện
///   chuyển) và bảng chính là chỗ rẻ nhất để lấy nốt phần thiếu — bắt hội thoại làm xong việc đó trước rồi
///   mới bày bảng là bỏ đúng lý do bảng tồn tại.</item>
/// </list>
///
/// <para>
/// <b>KHÔNG đòi «Thông báo / nhắc nhở» chạm tới</b> — điều kiện này đã bị gỡ khi nhóm ấy chuyển sang được
/// chốt bằng <see cref="NotificationMapGate"/>. Nhóm thông báo nay không còn được hỏi bằng câu hỏi, nên nó
/// đứng ở <c>[CHƯA HỎI]</c> suốt buổi; giữ điều kiện cũ là khóa chết cổng này — mà bảng đối tượng lại
/// chính là nguồn DÒNG của bảng thông báo, nên cả hai cùng không bao giờ mở. Cùng hình dạng vòng khóa kín
/// mà <see cref="PermissionMatrixGate"/> đã phải né.
/// </para>
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
               && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.Lifecycle);
    }
}

/// <summary>
/// Cổng TẤT ĐỊNH cho BẢNG THÔNG BÁO / NHẮC NHỞ — bảng THỨ NĂM và là bảng cuối cùng của buổi phỏng vấn.
///
/// <para>
/// Nhóm «Thông báo / nhắc nhở» là nhóm THỨ HAI không được hỏi bằng câu hỏi, và vì đúng lý do của nhóm phân
/// quyền. Chuẩn <c>[RÕ]</c> của nó đòi hai vế GHÉP ĐƯỢC với nhau — mỗi loại sự kiện biết ai là người nhận
/// của RIÊNG nó — trong khi hình dạng tự nhiên của câu hỏi lại tách chúng ra làm hai câu rời ("vai trò nào
/// cần nhận email?" + "sự kiện nào cần gửi?"). Ca thật đã ghi ở <c>requirement-coverage.v3.md</c>: người
/// dùng bấm bốn chip vai trò, dòng được nâng <c>[RÕ]</c>, và tài liệu đóng băng thành "mọi thay đổi trạng
/// thái gửi cho cả bốn nhóm" — tức mỗi lần một bản kế hoạch đổi trạng thái thì cả nhà máy nhận email.
/// </para>
///
/// <para>
/// Điều kiện mở, cả ba đều bắt buộc:
/// </para>
/// <list type="number">
///   <item><b>Chưa chốt bảng.</b></item>
///   <item><b>Bảng PHÂN QUYỀN đã chốt</b> — danh sách người nhận cần các VAI TRÒ người dùng đã tự tay duyệt
///   (<see cref="PermissionMatrixBuilder.Roles"/>). Đây cũng là thứ làm bảng này thật sự đứng CUỐI: xét
///   theo bảng đã chốt chứ không theo thứ tự ưu tiên trong <see cref="InterviewTableGate.Select"/>, vì một
///   lượt bày bảng phân quyền hỏng (model không trả nổi bảng dùng được) không được phép để bảng thông báo
///   chen lên trước với một danh sách vai trò rỗng.</item>
///   <item><b>Có ít nhất một dòng gieo được</b> (<see cref="NotificationMapBuilder.SeedRows"/> — các chuyển
///   trạng thái của bảng đối tượng đã chốt). Không có vòng đời nào thì không có sự kiện nào để hỏi, và bảng
///   này KHÔNG bao giờ được bày: nhóm quay về đường hỏi bằng câu hỏi (xem lệnh cấm hỏi lẻ trong
///   <c>BAChatService</c>, nó tự tắt đúng ở ca này). Thiếu đường thoát đó thì ứng dụng danh mục thuần —
///   không đối tượng nào có trạng thái — kẹt vĩnh viễn: không bảng nào bày ra, không câu hỏi nào được
///   phép, nhóm không bao giờ <c>[RÕ]</c>, nút "Write Requirement" không bao giờ sáng.</item>
/// </list>
/// </summary>
public static class NotificationMapGate
{
    /// <summary>Đã tới lúc bày bảng thông báo cho dự án này chưa.</summary>
    public static bool ShouldAsk(Project project)
        => ShouldAsk(project.NotificationMap, project.PermissionMatrix, project.EntityMap);

    /// <summary>Bản thuần dữ liệu — để test và để gọi từ nơi không có entity.</summary>
    public static bool ShouldAsk(string? notificationMapJson, string? permissionMatrixJson, string? entityMapJson)
    {
        if (NotificationMapBuilder.IsConfirmed(notificationMapJson))
            return false;
        if (!PermissionMatrixGate.IsConfirmed(permissionMatrixJson))
            return false;

        return NotificationMapBuilder.SeedRows(entityMapJson).Count > 0;
    }
}
