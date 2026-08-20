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

    /// <summary>Bảng BÁO CÁO / THỐNG KÊ: mỗi báo cáo một dòng, và mỗi dòng còn tích là một MÀN HÌNH.</summary>
    ReportMap,

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
/// bảng tính vì cùng lý do. Với sáu bảng thì việc nhường không còn là một ngoại lệ viết tay được nữa, nên
/// nó thành một hàm.
/// </para>
///
/// <para>
/// <b>Thứ tự ưu tiên là thứ tự PHỤ THUỘC, không phải thứ tự tiện tay.</b> Thứ tự là
/// <c>luồng → đối tượng → báo cáo → màn hình → phân quyền → thông báo</c>, và nó suy ra từ một câu hỏi duy
/// nhất: bảng nào ĐẺ RA màn hình thì phải đứng trước bảng chốt phạm vi màn hình.
/// </para>
///
/// <para>
/// Luồng trước, vì mọi bảng sau đều trỏ về bước luồng: bảng màn hình có ô "chức năng này phục vụ bước nào",
/// bảng đối tượng lấy điều kiện chuyển trạng thái từ chính các bước. Đối tượng rồi báo cáo đứng TRƯỚC màn
/// hình, vì cả hai là NGUỒN màn hình chứ không phải người tiêu thụ: mỗi thông tin kiểu chọn có nguồn "ứng
/// dụng tự quản lý" đẻ ra một màn hình quản lý danh mục
/// (<see cref="EntityMapBuilder.ManagedListScreens(string?)"/>), và mỗi báo cáo còn giữ là một màn hình
/// (<see cref="ReportMapBuilder.ReportScreens(string?)"/>) — cả hai gieo thẳng vào
/// <c>Project.PlannedScope</c>, tức vào chính các DÒNG của bảng màn hình. Màn hình sau đó là chỗ người dùng
/// rà TRỌN phạm vi đúng MỘT lần. Phân quyền gần cuối, vì các DÒNG của nó là màn hình — hỏi trước khi phạm
/// vi màn hình đứng yên thì bảng thiếu nửa số dòng, mà quyền của một màn hình chưa tồn tại thì không ai trả
/// lời được. Thông báo CUỐI CÙNG, vì nó vay cả hai chiều: các DÒNG là chuyển trạng thái của bảng đối tượng,
/// còn danh sách người nhận cần các VAI TRÒ của bảng phân quyền — vai trò của ứng dụng đang thiết kế chỉ
/// tồn tại trong hội thoại, không có bảng nào trong DB liệt kê chúng.
/// </para>
///
/// <para>
/// <b>Vì sao thứ tự cũ (màn hình trước đối tượng) là một lỗi, không phải một lựa chọn.</b> Lý do cũ ghi ở
/// đây là "cái người dùng nhìn thấy trên màn hình quyết định thông tin nào thật sự cần lưu" — nhưng chiều
/// đó chưa bao giờ tồn tại trong code: <see cref="ScreenScopeMapBuilder"/> không đọc <c>EntityMap</c>, khối
/// lệnh bày bảng đối tượng không nhắc tới bảng màn hình, và một dòng của bảng màn hình còn không chở nổi
/// một trường thông tin nào để mà quyết định. Chiều CÓ THẬT thì chạy ngược lại và chạy tất định (hai hàm
/// gieo màn hình nêu trên). Hệ quả của thứ tự cũ, đo được trên dự án thật: người dùng chốt bảng màn hình
/// như một phạm vi trọn vẹn, mấy lượt sau bảng đối tượng gieo thêm năm màn hình quản lý danh mục, bảng màn
/// hình phải mở lại, và <see cref="RequirementConflictService"/> bắn một mâu thuẫn ("trước đây anh/chị xác
/// nhận đây là toàn bộ màn hình…") — tiêu một suất trong tối đa 5 mâu thuẫn cho một xung đột do chính thứ
/// tự đẻ ra, và bắt người dùng rà cùng một bảng hai lần. Đường mở lại của
/// <see cref="ScreenScopeGate"/> vẫn còn nguyên, nhưng nay nó lùi về đúng vai LƯỚI AN TOÀN (phạm vi trôi vì
/// hội thoại sau lúc chốt) thay vì là đường chính của một luồng biết trước là sẽ trôi.
/// </para>
///
/// <para>
/// <b>Vì sao BỐN bảng GIỮA không được là điều kiện để một nhóm lên <c>[RÕ]</c>.</b> Hai nhóm cuối («Phân
/// quyền theo nghiệp vụ» và «Thông báo / nhắc nhở») có luật khắt khe một chiều — chưa có bảng thì không
/// bao giờ <c>[RÕ]</c> — và luật đó đúng vì cả hai KHÔNG được hỏi bằng câu hỏi. Bốn nhóm của các bảng giữa
/// (luồng, đối tượng, báo cáo, màn hình) thì có: chúng được hỏi suốt buổi, và bảng chỉ XÁC NHẬN LẠI thứ
/// hội thoại đã trả lời. Với bảng BÁO CÁO điều đó còn là điều kiện MỞ CỔNG chứ không chỉ là một lựa chọn
/// thiết kế — xem <see cref="ReportMapGate"/>: bảng chỉ bày ra khi nhóm đã <c>[RÕ]</c>, vì một bảng báo cáo
/// TRỐNG bắt người dùng nghiệp vụ tự chẻ câu chuyện của họ thành bốn cột trước khi gõ được chữ nào. Áp luật một chiều
/// cho chúng là dựng một vòng khóa kín — cổng đòi nhóm <c>[RÕ]</c> mới mở, bản đồ đòi có bảng mới
/// <c>[RÕ]</c>, và không bên nào đi trước được. Đó chính là cái bẫy mà <see cref="PermissionMatrixGate"/>
/// né bằng cách cố ý BỎ QUA hai dòng đó khi xét.
/// </para>
///
/// <para>
/// Hệ quả: khi <see cref="PermissionMatrixGate"/> mở (mọi nhóm áp dụng khác đã <c>[RÕ]</c>), điều kiện bản
/// đồ của cả bốn cổng kia đương nhiên cũng đã đúng — nên bảng nào chưa chốt sẽ lần lượt được hỏi TRƯỚC nó,
/// và không cổng nào cần biết cổng khác tồn tại. Riêng <see cref="ReportMapGate"/> còn một vế thứ hai
/// (bảng đối tượng đã CHỐT) nhưng vế đó không mở thêm đường vòng nào: bảng đối tượng chưa chốt thì
/// <see cref="EntityMapGate"/> đang mở và thắng ưu tiên, nên lượt ấy vẫn là một lượt bày bảng. Bảng phân quyền rồi bảng thông báo là hai bảng cuối, và
/// nhóm của cả hai chỉ <c>[RÕ]</c> sau khi bảng được chốt, nên không có đường nào soạn tài liệu mà bỏ qua
/// các bảng này.
/// </para>
///
/// <para>
/// <b>Thứ tự ưu tiên KHÔNG tự nó xếp được thứ tự.</b> Danh sách dưới đây chỉ phân xử khi hai cổng CÙNG mở;
/// cổng đứng trước còn ĐÓNG vì bản đồ chưa đủ thì cổng sau vẫn bày trước và thứ tự phụ thuộc bị đảo trong
/// im lặng. Vì vậy mỗi cổng đứng sau MƯỢN điều kiện bản đồ của cổng đứng trước cho lần bày ĐẦU:
/// <see cref="EntityMapGate"/> mượn <see cref="FlowMapGate.CoverageReady"/>, còn
/// <see cref="ScreenScopeGate"/> mượn <see cref="EntityMapGate.CoverageDecided"/> (đã bao điều kiện của
/// cổng luồng) cộng nhóm «Báo cáo / thống kê» đã chạm tới. Hai ranh giới cố ý: chỉ mượn ĐIỀU KIỆN BẢN ĐỒ,
/// không đòi bảng đứng trước đã CHỐT — treo một cổng vào artifact do model sinh ra là đổi một lượt bày bảng
/// hỏng lấy một chuỗi kẹt vĩnh viễn; và cổng sau chờ cổng trước NGÃ NGŨ chứ không chờ nó SẴN SÀNG — một
/// nhóm ở <c>[KHÔNG ÁP DỤNG]</c> nghĩa là bảng đứng trước sẽ không bao giờ tới, và chờ nó là xoá luôn bảng
/// đứng sau khỏi buổi phỏng vấn.
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

        // luồng → đối tượng → báo cáo → màn hình → phân quyền → thông báo. Đối tượng và báo cáo đứng TRƯỚC
        // màn hình vì cả hai gieo màn hình vào PlannedScope, tức vào chính các DÒNG của bảng màn hình — xem
        // ghi chú class.
        if (FlowMapGate.ShouldAsk(project))
            return InterviewTableKind.FlowMap;
        if (EntityMapGate.ShouldAsk(project))
            return InterviewTableKind.EntityMap;
        if (ReportMapGate.ShouldAsk(project))
            return InterviewTableKind.ReportMap;
        if (ScreenScopeGate.ShouldAsk(project))
            return InterviewTableKind.ScreenScope;
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
        public const string Report = "Báo cáo / thống kê";
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

        return CoverageReady(items);
    }

    /// <summary>
    /// Phần điều kiện của cổng này nằm ở BẢN ĐỒ BAO PHỦ, tách riêng vì <see cref="EntityMapGate"/> phải đọc
    /// lại đúng nó (và <see cref="ScreenScopeGate"/> đọc gián tiếp qua
    /// <see cref="EntityMapGate.CoverageReady"/>): bảng đứng sau chỉ được bày LẦN ĐẦU khi cổng luồng đã đủ
    /// điều kiện mở, để thứ tự ưu tiên ở <see cref="InterviewTableGate.Select"/> có cơ hội đặt bảng luồng
    /// lên trước. Hai nơi cùng một điều kiện mà chép tay hai bản thì lần sửa sau chỉ sửa một bản, và cái
    /// sai quay lại đúng hình dạng cũ — một bảng đi trước bảng luồng.
    /// </summary>
    internal static bool CoverageReady(IReadOnlyList<CoverageMapItem> items)
        => InterviewTableGate.IsClear(items, InterviewTableGate.Groups.MainFlow)
           && InterviewTableGate.IsClear(items, InterviewTableGate.Groups.Roles)
           && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.ExceptionFlow);
}

/// <summary>
/// Cổng TẤT ĐỊNH cho BẢNG MÀN HÌNH — bảng THỨ TƯ, đứng sau ba bảng gieo ra màn hình và ngay trước bảng
/// phân quyền.
///
/// <para>
/// Đây là chỗ người dùng rà TRỌN phạm vi màn hình đúng MỘT lần, nên nó phải đứng sau mọi nguồn màn hình:
/// bảng đối tượng (mỗi danh mục "ứng dụng tự quản lý" là một màn hình quản lý) và bảng báo cáo (mỗi báo cáo
/// còn giữ là một màn hình) đều gieo vào <c>Project.PlannedScope</c> lúc chốt, tức vào chính các DÒNG của
/// bảng này. Đứng trước chúng — thứ tự cũ — thì bảng tự giới thiệu là phạm vi trọn vẹn, người dùng gật, rồi
/// hệ thống tự thêm màn hình sau lưng họ; xem ghi chú của <see cref="InterviewTableGate"/> cho ca thật và
/// cái giá (một mâu thuẫn giả ở <see cref="RequirementConflictService"/> + một lượt rà lặp).
/// </para>
///
/// <para>
/// Điều kiện mở:
/// </para>
/// <list type="number">
///   <item><b>Chưa chốt bảng — HOẶC đã chốt mà có màn hình MỚI lộ ra sau đó.</b> Xem mục dưới.</item>
///   <item><b>Phạm vi đã có mục</b> (<c>Project.PlannedScope</c>) — các DÒNG của bảng chính là nó.</item>
///   <item><b>Lần bày ĐẦU TIÊN: mọi cổng đứng trước đã NGÃ NGŨ</b> —
///   <see cref="EntityMapGate.CoverageDecided"/> (đã bao <see cref="FlowMapGate.CoverageReady"/>, cộng «Dữ
///   liệu / danh mục chính» và «Vòng đời &amp; trạng thái» đã chạm tới) <b>và nhóm «Báo cáo / thống kê» đã
///   được CHẠM TỚI</b>. "Ngã ngũ" chứ không phải "sẵn sàng": xem đoạn dưới. Đường MỞ LẠI chỉ đòi «Chức năng
///   &amp; luồng nghiệp vụ chính» <c>[RÕ]</c>.</item>
/// </list>
///
/// <para>
/// <b>Vì sao lần bày đầu phải mượn điều kiện của cổng đứng trước.</b> Thứ tự ưu tiên ở
/// <see cref="InterviewTableGate.Select"/> chỉ phân xử được khi hai cổng cùng mở; nó KHÔNG cứu được ca cổng
/// đứng trước còn ĐÓNG vì bản đồ chưa đủ. Điều kiện đời đầu chỉ đòi «Chức năng &amp; luồng nghiệp vụ chính»
/// <c>[RÕ]</c> — nhóm này thường lên <c>[RÕ]</c> ngay ở lượt người dùng kể luồng, trong khi vai trò và
/// ngoại lệ còn phải hỏi thêm vài lượt nữa. Ca thật (dự án JD Libary 1): bảng màn hình bày ở lượt 12, bảng
/// luồng mãi lượt 20 mới tới. Thiệt hại nằm ở ô "màn này phục vụ bước nào" — lượt 12 chưa có bước nào để
/// gắn, nên cả cột ra rỗng; tới khi luồng chốt xong thì phép kiểm
/// <see cref="ScreenScopeMapBuilder.UncoveredActions"/> báo gần như MỌI bước chưa ai phụ trách, và người
/// dùng phải rà bảng màn hình lần thứ hai chỉ vì lần đầu bày quá sớm. Cùng lỗ hổng đó, cùng cách vá đó, nay
/// áp cho cả hai cổng đứng trước.
/// </para>
///
/// <para>
/// <b>Chờ "NGÃ NGŨ", không chờ "SẴN SÀNG" — cả ba vế đều chỉ đòi CHẠM TỚI.</b> Một nhóm ở
/// <c>[KHÔNG ÁP DỤNG]</c> nghĩa là bảng của nó sẽ KHÔNG BAO GIỜ tới: dự án không có danh mục nào, hoặc
/// không cần báo cáo nào. Bắt bảng màn hình chờ một bảng như thế là xoá nó khỏi buổi phỏng vấn — mà
/// <see cref="PermissionMatrixGate"/> lại coi <c>[KHÔNG ÁP DỤNG]</c> là đã trả lời và cứ thế mở, nên hậu
/// quả không phải một chỗ kẹt thấy được mà là bảng phân quyền quay về đứng trên <c>PlannedScope</c> thô,
/// đúng cái nền chưa ai duyệt mà bảng này sinh ra để vá. Ngược lại, nhóm còn
/// <c>[MỘT PHẦN]</c>/<c>[CHƯA HỎI]</c> nghĩa là bảng ấy vẫn đang trên đường ⇒ chờ; và chờ ở đó không dựng
/// thêm chỗ kẹt nào vì <see cref="PermissionMatrixGate"/> vốn đã đòi mọi nhóm áp dụng ngã ngũ mới mở. Nhóm
/// ở <c>[RÕ]</c> thì cổng đứng trước cùng mở một lượt và thắng ưu tiên ở
/// <see cref="InterviewTableGate.Select"/>, nên không có khe nào để bảng màn hình chen lên trước.
/// </para>
///
/// <para>
/// <b>Vẫn KHÔNG đòi bảng đứng trước đã CHỐT.</b> Đó là ranh giới cố ý: điều kiện thuần bản đồ bao phủ,
/// không treo vào một artifact do model sinh ra. Model không trả nổi bảng đối tượng dùng được (structured
/// output tắt) thì cổng đối tượng cứ mở lại mỗi lượt và <see cref="InterviewTableGate.Select"/> giữ nguyên
/// quyền ưu tiên của nó — thêm một dây trói vào <c>EntityMap != null</c> ở đây chỉ đổi một lượt hỏng thành
/// một chuỗi kẹt vĩnh viễn.
/// </para>
///
/// <para>
/// Điều kiện mới KHÔNG dựng thêm khóa chéo: nó là tập CON điều kiện của
/// <see cref="PermissionMatrixGate"/> (cổng đó đòi mọi nhóm áp dụng <c>[RÕ]</c>), nên bất biến "cổng phân
/// quyền mở ⇒ ba cổng trước cũng mở" vẫn đúng — <c>InterviewTableGateTests</c> khóa nó lại.
/// </para>
///
/// <para>
/// <b>Cổng DUY NHẤT trong bốn cổng mở lại được sau khi đã chốt</b> — vì nó là cổng duy nhất mà phạm vi có
/// thể trôi tiếp sau lượt chốt. Từ khi hai bảng gieo màn hình được đưa lên TRƯỚC bảng này, đường mở lại
/// không còn gánh phần trôi biết-trước-là-sẽ-xảy-ra (danh mục và báo cáo nay có mặt ngay ở lần bày đầu); nó
/// lùi về đúng vai lưới an toàn cho phần trôi THẬT: một màn hình lộ ra từ hội thoại sau lượt chốt. Ca thật
/// (dự án Learning and Development 7): bảng chốt ở lượt 23; tới lượt
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

        var confirmed = ScreenScopeMapBuilder.IsConfirmed(screenScopeJson);
        // Đã chốt ⇒ chỉ mở lại khi có màn hình MỚI lộ ra sau lúc chốt. Không có mục mới nào thì bảng đã là
        // câu trả lời của người dùng, và bày lại một bảng y hệt là bắt họ làm lại việc vừa làm.
        if (confirmed && ScreenScopeMapBuilder.NewScreens(screenScopeJson, plannedScope).Count == 0)
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        // LẦN BÀY ĐẦU nhường cả ba bảng đứng trước: chờ tới khi mỗi cổng trong số đó ĐÃ NGÃ NGŨ — hoặc đủ
        // điều kiện mở (cùng mở một lượt, rồi thứ tự ưu tiên ở Select phân xử), hoặc sẽ không bao giờ mở vì
        // nhóm của nó đứng ở [KHÔNG ÁP DỤNG]. Chờ "sẵn sàng" thay vì "ngã ngũ" là để một dự án không có
        // danh mục/không có báo cáo mất hẳn bảng màn hình. Xem phần đầu class cho ca thật.
        if (!confirmed)
            return EntityMapGate.CoverageDecided(items)
                   && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.Report);

        // ĐƯỜNG MỞ LẠI giữ nguyên điều kiện cũ, và đây không phải sơ hở: tới đây thì bảng đã chốt một lần,
        // tức mọi điều kiện của lần bày đầu ĐÃ từng đúng, và việc của lượt này chỉ là mấy màn hình vừa lộ
        // ra. Đòi lại cả bộ là để một nhóm bị lượt distill hạ xuống [MỘT PHẦN] chặn mất đường thu hồi phần
        // phạm vi trôi — mà chính nó mới là lý do cổng này được phép mở lại.
        return InterviewTableGate.IsClear(items, InterviewTableGate.Groups.MainFlow);
    }
}

/// <summary>
/// Cổng TẤT ĐỊNH cho BẢNG ĐỐI TƯỢNG NGHIỆP VỤ — bảng THỨ HAI, ngay sau bảng luồng và TRƯỚC bảng màn hình.
///
/// <para>
/// Hai lượt bảng liền nhau ở đây là CỐ Ý: người dùng vừa gật một chuỗi bước, và cột "khi nào chuyển vào"
/// của bảng này lấy thẳng điều kiện từ chính các bước đó — đặt cách xa nhau thì cột ấy mất ngữ cảnh. Bảng
/// còn phải đứng trước bảng màn hình vì nó là một NGUỒN màn hình: mỗi thông tin kiểu chọn có nguồn "ứng
/// dụng tự quản lý" đẻ ra một màn hình quản lý danh mục
/// (<see cref="EntityMapBuilder.ManagedListScreens(string?)"/>) và gieo thẳng vào <c>PlannedScope</c> lúc
/// chốt. Xem ghi chú của <see cref="InterviewTableGate"/>.
/// </para>
///
/// <para>
/// Điều kiện mở:
/// </para>
/// <list type="number">
///   <item><b>Chưa chốt bảng nào.</b></item>
///   <item><b>Cổng luồng đã ĐỦ ĐIỀU KIỆN MỞ</b> (<see cref="FlowMapGate.CoverageReady"/>) — cùng luật
///   nhường-thứ-tự mà <see cref="ScreenScopeGate"/> áp lên chính cổng này, xem đoạn dưới.</item>
///   <item><b>«Dữ liệu / danh mục chính» đã <c>[RÕ]</c></b> — đây là nhóm chở phần lớn nội dung bảng.</item>
///   <item><b>«Vòng đời &amp; trạng thái» đã được CHẠM TỚI</b> — cột trạng thái của bảng. Chỉ đòi chạm tới
///   chứ không đòi <c>[RÕ]</c>: chuẩn <c>[RÕ]</c> của nhóm này khắt khe (gọi tên trạng thái + điều kiện
///   chuyển) và bảng chính là chỗ rẻ nhất để lấy nốt phần thiếu — bắt hội thoại làm xong việc đó trước rồi
///   mới bày bảng là bỏ đúng lý do bảng tồn tại.</item>
/// </list>
///
/// <para>
/// <b>Vì sao cổng này phải mượn điều kiện của cổng luồng.</b> Cùng lỗ hổng mà <see cref="ScreenScopeGate"/>
/// vá, chỉ khác chỗ: hai nhóm của cổng này («Dữ liệu / danh mục chính», «Vòng đời &amp; trạng thái») rời
/// hẳn nhóm vai trò, nên có ca dữ liệu và vòng đời đã rõ trong khi vai trò còn <c>[MỘT PHẦN]</c> — lúc đó
/// cổng luồng còn ĐÓNG và bảng đối tượng bày ra đầu tiên, trong khi cột "khi nào chuyển vào" của nó lấy
/// điều kiện từ chính các bước luồng. Điều kiện này khép nốt khoảng hở đó: khi cổng đối tượng mở thì cổng
/// luồng hoặc cũng mở (và thắng theo thứ tự ưu tiên ở <see cref="InterviewTableGate.Select"/>), hoặc đã
/// đóng vì bảng luồng chốt rồi.
/// </para>
///
/// <para>
/// <b>KHÔNG đòi <c>PlannedScope</c> có mục</b>, khác <see cref="ScreenScopeGate"/>: bảng này không lấy dòng
/// từ phạm vi màn hình, và một dự án chưa chắt được mục phạm vi nào vẫn phải trả lời được "ứng dụng lưu hồ
/// sơ gì". Đây cũng là chỗ hai cổng tách nhau khi phạm vi còn rỗng — cổng màn hình đóng vì không có DÒNG
/// nào để hỏi, còn cổng này vẫn mở và bảng đối tượng đi trước, đúng thứ tự phụ thuộc.
/// </para>
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

        return CoverageReady(items);
    }

    /// <summary>
    /// Phần điều kiện của cổng này nằm ở BẢN ĐỒ BAO PHỦ, tách riêng vì <see cref="ScreenScopeGate"/> phải
    /// đọc lại đúng nó: bảng màn hình chỉ được bày LẦN ĐẦU khi cổng đối tượng đã đủ điều kiện mở, để thứ tự
    /// ưu tiên ở <see cref="InterviewTableGate.Select"/> có cơ hội đặt bảng đối tượng lên trước. Cùng lý do
    /// tách <see cref="FlowMapGate.CoverageReady"/>: hai bản chép tay thì lần sửa sau chỉ sửa một bản, và
    /// cái sai quay lại đúng hình dạng cũ — bảng màn hình đi trước bảng đối tượng rồi phải mở lại.
    ///
    /// <para>
    /// Nó BAO cả <see cref="FlowMapGate.CoverageReady"/>, nên cổng màn hình mượn một hàm là đủ cho cả chuỗi
    /// luồng → đối tượng → màn hình. Vẫn thuần bản đồ bao phủ: không cổng nào trong ba cổng đầu treo vào một
    /// bảng do model sinh ra.
    /// </para>
    /// </summary>
    internal static bool CoverageReady(IReadOnlyList<CoverageMapItem> items)
        => FlowMapGate.CoverageReady(items)
           && InterviewTableGate.IsClear(items, InterviewTableGate.Groups.Data)
           && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.Lifecycle);

    /// <summary>
    /// Cổng này đã NGÃ NGŨ: hoặc đủ điều kiện mở, hoặc sẽ KHÔNG BAO GIỜ mở vì một nhóm của nó đứng ở
    /// <c>[KHÔNG ÁP DỤNG]</c>. Đây mới là thứ <see cref="ScreenScopeGate"/> phải chờ, chứ không phải
    /// <see cref="CoverageReady"/>.
    ///
    /// <para>
    /// <b>Vì sao chờ "ngã ngũ" chứ không chờ "sẵn sàng".</b> «Dữ liệu / danh mục chính» ở
    /// <c>[KHÔNG ÁP DỤNG]</c> làm <see cref="CoverageReady"/> false vĩnh viễn (nó đòi <c>[RÕ]</c>), trong khi
    /// <see cref="PermissionMatrixGate"/> lại coi <c>[KHÔNG ÁP DỤNG]</c> là đã trả lời và cứ thế mở. Chờ
    /// nhầm vế thì bảng màn hình biến mất khỏi buổi phỏng vấn và bảng phân quyền quay về đứng trên
    /// <c>PlannedScope</c> thô — đúng cái nền chưa ai duyệt mà bảng màn hình sinh ra để vá. Chờ đúng vế thì
    /// thứ tự vẫn được giữ ở mọi ca bảng đối tượng CÓ THỂ tới: nhóm còn <c>[MỘT PHẦN]</c>/<c>[CHƯA HỎI]</c>
    /// là bảng ấy vẫn đang trên đường, nên bảng màn hình chờ.
    /// </para>
    /// </summary>
    internal static bool CoverageDecided(IReadOnlyList<CoverageMapItem> items)
        => FlowMapGate.CoverageReady(items)
           && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.Data)
           && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.Lifecycle);
}

/// <summary>
/// Cổng TẤT ĐỊNH cho BẢNG BÁO CÁO / THỐNG KÊ — bảng THỨ BA, đứng giữa bảng đối tượng và bảng màn hình.
///
/// <para>
/// Chỗ đứng ấy là hệ quả của hai chiều phụ thuộc gặp nhau: ô "lấy số từ" của mỗi báo cáo trỏ về một đối
/// tượng ĐÃ chốt (nên phải đứng sau bảng đối tượng), còn mỗi báo cáo còn giữ lại là một MÀN HÌNH gieo vào
/// <c>PlannedScope</c> (<see cref="ReportMapBuilder.ReportScreens(string?)"/>) nên phải đứng TRƯỚC bảng màn
/// hình — hỏi sau thì các màn hình báo cáo không có mặt ở lần bày đầu của bảng màn hình, và người dùng vừa
/// chốt xong một phạm vi "trọn vẹn" đã thấy nó thay đổi. Xem ghi chú của <see cref="InterviewTableGate"/>.
/// </para>
///
/// <para>
/// Điều kiện mở, cả ba đều bắt buộc:
/// </para>
/// <list type="number">
///   <item><b>Chưa chốt bảng.</b></item>
///   <item><b>Bảng ĐỐI TƯỢNG đã chốt</b> — ô "lấy số từ" của mỗi báo cáo phải trỏ về một đối tượng người
///   dùng vừa tự tay rà (<see cref="EntityMapBuilder.EntityNames"/>). Đây là vế treo vào một artifact, thứ
///   ba cổng đầu cố tình tránh; ở đây nó an toàn vì nó không mở thêm đường vòng nào: bảng đối tượng chưa
///   chốt thì <see cref="EntityMapGate"/> đang mở và thắng ưu tiên, nên lượt ấy vẫn là một lượt bày bảng
///   chứ không phải một lượt câm. Cùng khuôn với <see cref="NotificationMapGate"/>, cổng cũng xét theo
///   bảng đã chốt.</item>
///   <item><b>Nhóm «Báo cáo / thống kê» đã <c>[RÕ]</c></b> — và đây là điều kiện QUAN TRỌNG NHẤT của cổng
///   này, không phải một chi tiết về nhịp. Bảng của các nhóm khác được bày ra để BA <i>ráp lại</i> thứ hội
///   thoại đã kể; bảng báo cáo cũng vậy, nhưng nó là bảng duy nhất mà cám dỗ "bày sớm cho người dùng tự
///   điền" là có thật, vì hình dạng câu trả lời (một danh sách) trông như đã sẵn sàng cho một cái bảng
///   trống. Bày trống là bắt người dùng nghiệp vụ tự chẻ câu chuyện của họ thành bốn cột trước khi gõ được
///   chữ nào — khó hơn hẳn kể tự do, nên thứ thu về ÍT hơn cả cái ô text nó thay thế, và đó là đúng thái
///   cực mà luật "ô ý nghĩa do BA điền sẵn" của bảng cột đã bỏ đi một lần rồi.</item>
/// </list>
///
/// <para>
/// <b>Nhóm này vẫn được hỏi bằng CÂU HỎI</b>, khác hẳn hai nhóm chốt-bằng-bảng ở cuối — nên điều kiện thứ
/// ba không phải một vòng khóa kín: câu mở đầu của nhóm vẫn nằm ở
/// <see cref="CoverageGroupOpeners"/> và hội thoại vẫn là đường đưa nhóm lên <c>[RÕ]</c>. Bảng chỉ CHỐT
/// LẠI cho có ranh giới. Cùng lý do, <c>[KHÔNG ÁP DỤNG]</c> (người dùng nói rõ không cần báo cáo nào) KHÔNG
/// phải <c>[RÕ]</c> ⇒ cổng không bao giờ mở, và đó là đường thoát cho dự án không có báo cáo: không có nó
/// thì một ứng dụng thuần nhập liệu vẫn bị bày ra một bảng báo cáo rỗng ở cuối buổi.
/// </para>
/// </summary>
public static class ReportMapGate
{
    /// <summary>Đã tới lúc bày bảng báo cáo cho dự án này chưa.</summary>
    public static bool ShouldAsk(Project project)
        => ShouldAsk(project.RequirementCoverageMap, project.ReportMap, project.EntityMap);

    /// <summary>Bản thuần dữ liệu — để test và để gọi từ nơi không có entity.</summary>
    public static bool ShouldAsk(string? coverageMap, string? reportMapJson, string? entityMapJson)
    {
        if (ReportMapBuilder.IsConfirmed(reportMapJson))
            return false;
        if (!EntityMapBuilder.IsConfirmed(entityMapJson))
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        return InterviewTableGate.IsClear(items, InterviewTableGate.Groups.Report);
    }
}

/// <summary>
/// Cổng TẤT ĐỊNH cho BẢNG THÔNG BÁO / NHẮC NHỞ — bảng THỨ SÁU và là bảng cuối cùng của buổi phỏng vấn.
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
