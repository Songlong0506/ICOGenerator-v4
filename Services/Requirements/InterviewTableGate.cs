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
/// <c>luồng → báo cáo → màn hình → phân quyền → thông báo</c>, và nó suy ra từ một câu hỏi duy nhất:
/// bảng nào ĐẺ RA màn hình thì phải đứng trước bảng chốt phạm vi màn hình.
/// </para>
///
/// <para>
/// Luồng trước, vì mọi bảng sau đều trỏ về bước luồng: bảng màn hình có ô "chức năng này phục vụ bước nào".
/// Báo cáo đứng TRƯỚC màn hình vì nó là một NGUỒN màn hình chứ không phải người tiêu thụ: mỗi báo cáo còn
/// giữ là một màn hình (<see cref="ReportMapBuilder.ReportScreens(string?)"/>) gieo thẳng vào các DÒNG của
/// bảng màn hình lúc chốt. Màn hình sau đó là chỗ người dùng rà TRỌN phạm vi đúng MỘT lần. Phân quyền gần
/// cuối, vì các DÒNG của nó là màn hình — hỏi trước khi phạm vi màn hình đứng yên thì bảng thiếu nửa số
/// dòng, mà quyền của một màn hình chưa tồn tại thì không ai trả lời được. Thông báo CUỐI CÙNG, vì danh
/// sách người nhận cần các VAI TRÒ của bảng phân quyền — vai trò của ứng dụng đang thiết kế chỉ tồn tại
/// trong hội thoại, không có bảng nào trong DB liệt kê chúng.
/// </para>
///
/// <para>
/// <b>Không có bảng đối tượng nghiệp vụ.</b> Mô hình dữ liệu (đối tượng nào có thông tin nào, bắt buộc hay
/// không, chọn từ danh sách nào) KHÔNG được chốt với người dùng nghiệp vụ: họ không rà nổi những ô đó, và
/// bấm gửi một bảng mình không hiểu là ký tên vào nó. Hai nhóm «Dữ liệu / danh mục chính» và «Vòng đời &amp;
/// trạng thái» vẫn được HỎI bằng câu hỏi; phần còn lại đi theo đường hội thoại — màn hình quản lý danh mục
/// do lượt chắt lọc phạm vi (<see cref="InterviewScopeService"/>) bắt từ lời người dùng, các dòng sự kiện
/// của bảng thông báo do model ráp từ các trạng thái hội thoại đã kể, và mô hình dữ liệu do bước sinh AI
/// Design Spec tự thiết kế (mục <c>## 8. Data Model Summary</c>).
/// </para>
///
/// <para>
/// <b>Vì sao BA bảng GIỮA không được là điều kiện để một nhóm lên <c>[RÕ]</c>.</b> Hai nhóm cuối («Phân
/// quyền theo nghiệp vụ» và «Thông báo / nhắc nhở») có luật khắt khe một chiều — chưa có bảng thì không
/// bao giờ <c>[RÕ]</c> — và luật đó đúng vì cả hai KHÔNG được hỏi bằng câu hỏi. Ba nhóm của các bảng giữa
/// (luồng, báo cáo, màn hình) thì có: chúng được hỏi suốt buổi, và bảng chỉ XÁC NHẬN LẠI thứ hội thoại đã
/// trả lời. Với bảng BÁO CÁO điều đó còn là điều kiện MỞ CỔNG chứ không chỉ là một lựa chọn thiết kế — xem
/// <see cref="ReportMapGate"/>: bảng chỉ bày ra khi nhóm đã <c>[RÕ]</c>, vì một bảng báo cáo TRỐNG bắt
/// người dùng nghiệp vụ tự chẻ câu chuyện của họ thành bốn cột trước khi gõ được chữ nào. Áp luật một chiều
/// cho chúng là dựng một vòng khóa kín — cổng đòi nhóm <c>[RÕ]</c> mới mở, bản đồ đòi có bảng mới
/// <c>[RÕ]</c>, và không bên nào đi trước được. Đó chính là cái bẫy mà <see cref="PermissionMatrixGate"/>
/// né bằng cách cố ý BỎ QUA hai dòng đó khi xét.
/// </para>
///
/// <para>
/// Hệ quả: khi <see cref="PermissionMatrixGate"/> mở (mọi nhóm áp dụng khác đã <c>[RÕ]</c>), điều kiện bản
/// đồ của cả ba cổng kia đương nhiên cũng đã đúng — nên bảng nào chưa chốt sẽ lần lượt được hỏi TRƯỚC nó,
/// và không cổng nào cần biết cổng khác tồn tại. Bảng phân quyền rồi bảng thông báo là hai bảng cuối, và
/// nhóm của cả hai chỉ <c>[RÕ]</c> sau khi bảng được chốt, nên không có đường nào soạn tài liệu mà bỏ qua
/// các bảng này.
/// </para>
///
/// <para>
/// <b>Thứ tự ưu tiên KHÔNG tự nó xếp được thứ tự.</b> Danh sách dưới đây chỉ phân xử khi hai cổng CÙNG mở;
/// cổng đứng trước còn ĐÓNG vì bản đồ chưa đủ thì cổng sau vẫn bày trước và thứ tự phụ thuộc bị đảo trong
/// im lặng. Vì vậy mỗi cổng đứng sau MƯỢN điều kiện bản đồ của cổng đứng trước: <see cref="ReportMapGate"/>
/// mượn <see cref="FlowMapGate.CoverageReady"/>, còn lần bày đầu của <see cref="ScreenScopeGate"/> mượn
/// <see cref="ScreenScopeGate.FirstShowReady"/> (đã bao điều kiện của cổng luồng). Hai ranh giới cố ý: chỉ
/// mượn ĐIỀU KIỆN BẢN ĐỒ, không đòi bảng đứng trước đã CHỐT — treo một cổng vào artifact do model sinh ra
/// là đổi một lượt bày bảng hỏng lấy một chuỗi kẹt vĩnh viễn; và cổng sau chờ cổng trước NGÃ NGŨ chứ không
/// chờ nó SẴN SÀNG — một nhóm ở <c>[KHÔNG ÁP DỤNG]</c> nghĩa là bảng đứng trước sẽ không bao giờ tới, và
/// chờ nó là xoá luôn bảng đứng sau khỏi buổi phỏng vấn.
/// </para>
/// </summary>
public static class InterviewTableGate
{
    /// <summary>Bảng của lượt này.</summary>
    public static InterviewTableKind Select(Project project)
    {
        // luồng → báo cáo → màn hình → phân quyền → thông báo. Báo cáo đứng TRƯỚC màn hình vì nó gieo màn
        // hình thẳng vào các DÒNG của bảng màn hình — xem ghi chú class.
        if (FlowMapGate.ShouldAsk(project))
            return InterviewTableKind.FlowMap;
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

    /// <summary>Nhãn nhóm trong bản đồ bao phủ, khớp <c>requirement-coverage.v5.md</c>.</summary>
    internal static class Groups
    {
        public const string Roles = "Đối tượng người dùng & vai trò";
        public const string MainFlow = "Chức năng & luồng nghiệp vụ chính";
        public const string ExceptionFlow = "Luồng ngoại lệ";
        public const string Data = "Dữ liệu / danh mục chính";
        public const string Lifecycle = "Vòng đời & trạng thái";
        // Nhãn nhóm thông báo có sẵn một chỗ giữ công khai (các gate khác đọc nó), nên ở đây chỉ trỏ tới —
        // hai bản của cùng một nhãn thì bản nào cũng có thể là bản bị sửa một mình.
        public const string Notification = NotificationMapGate.NotificationGroupLabel;
        public const string Report = "Báo cáo / thống kê";
    }

    /// <summary>
    /// Nhóm mang nhãn bắt đầu bằng <paramref name="prefix"/> đã ở trạng thái <c>[RÕ]</c> chưa. So khớp bằng
    /// TIỀN TỐ vì cùng lý do với <see cref="PermissionMatrixGate.PermissionGroupLabel"/>: một lượt distill
    /// viết chệch phần đuôi nhãn không được phép làm cổng câm vĩnh viễn. Nhóm không có mặt trong bản đồ ⇒
    /// false (fail-closed): bản đồ thiếu dòng là bản đồ hỏng, không phải một nhóm đã xong.
    /// </summary>
    internal static bool IsClear(IReadOnlyList<CoverageMapItem> items, string prefix)
        => Find(items, prefix)?.Status == CoverageStatus.Clear;

    /// <summary>
    /// Nhóm đã được CHẠM TỚI: <c>[RÕ]</c> hoặc <c>[KHÔNG ÁP DỤNG]</c>. Dùng cho các nhóm mà bảng chỉ chở
    /// một phần (ngoại lệ, vòng đời) — đòi <c>[RÕ]</c> ở đó là hoãn cổng tới tận lúc cổng phân quyền mở,
    /// tức dồn mọi bảng vào cuối buổi, đúng thứ thiết kế này muốn tránh.
    /// </summary>
    internal static bool IsSettled(IReadOnlyList<CoverageMapItem> items, string prefix)
    {
        var status = Find(items, prefix)?.Status;
        return status is CoverageStatus.Clear or CoverageStatus.NotApplicable;
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
    /// Phần điều kiện của cổng này nằm ở BẢN ĐỒ BAO PHỦ, tách riêng vì <see cref="ReportMapGate"/> phải đọc
    /// lại đúng nó (và <see cref="ScreenScopeGate"/> đọc gián tiếp qua
    /// <see cref="ScreenScopeGate.FirstShowReady"/>): bảng đứng sau chỉ được bày LẦN ĐẦU khi cổng luồng đã đủ
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
/// Cổng TẤT ĐỊNH cho BẢNG MÀN HÌNH — bảng THỨ BA, đứng sau bảng báo cáo và ngay trước bảng phân quyền.
///
/// <para>
/// Đây là chỗ người dùng rà TRỌN phạm vi màn hình đúng MỘT lần, nên nó phải đứng sau mọi nguồn màn hình:
/// bảng báo cáo (mỗi báo cáo còn giữ là một màn hình) gieo thẳng vào các DÒNG của bảng này lúc chốt, còn
/// màn hình quản lý danh mục do lượt chắt lọc phạm vi bắt từ hội thoại về nhóm «Dữ liệu / danh mục chính».
/// Đứng trước chúng thì bảng tự giới thiệu là phạm vi trọn vẹn, người dùng gật, rồi hệ thống tự thêm màn
/// hình sau lưng họ — một lượt rà lặp trên cùng một bảng.
/// </para>
///
/// <para>
/// Điều kiện mở:
/// </para>
/// <list type="number">
///   <item><b>Chưa chốt bảng — HOẶC đã chốt mà có màn hình MỚI lộ ra sau đó.</b> Xem mục dưới.</item>
///   <item><b>Bảng còn mục CHỜ DUYỆT</b> (<see cref="ScreenScopeMapBuilder.HasPending"/>) — một dòng, hoặc
///   một chức năng trên một dòng đã chốt, mà chưa ai rà. Đây là điều kiện chở cả hai ca trong một câu: bảng
///   trống thì không có gì để hỏi, bảng đã chốt trọn thì không còn gì để hỏi lại.</item>
///   <item><b>Lần bày ĐẦU TIÊN: mọi nguồn màn hình đứng trước đã NGÃ NGŨ</b> —
///   <see cref="FirstShowReady"/>: cổng luồng đã đủ điều kiện mở (<see cref="FlowMapGate.CoverageReady"/>),
///   «Dữ liệu / danh mục chính» và «Báo cáo / thống kê» đã được CHẠM TỚI. "Ngã ngũ" chứ không phải "sẵn
///   sàng": xem đoạn dưới. Đường MỞ LẠI chỉ đòi «Chức năng &amp; luồng nghiệp vụ chính» <c>[RÕ]</c>.</item>
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
/// dùng phải rà bảng màn hình lần thứ hai chỉ vì lần đầu bày quá sớm. Nhóm «Dữ liệu / danh mục chính» cũng
/// phải ngã ngũ vì cùng lý do: mỗi danh mục "ứng dụng tự quản lý" là một màn hình quản lý, và lời kể về danh
/// mục chính là thứ lượt chắt lọc phạm vi đọc để thêm màn hình ấy vào bảng trước lần bày đầu.
/// </para>
///
/// <para>
/// <b>Chờ "NGÃ NGŨ", không chờ "SẴN SÀNG" — hai nhóm dữ liệu và báo cáo chỉ đòi CHẠM TỚI.</b> Một nhóm ở
/// <c>[KHÔNG ÁP DỤNG]</c> nghĩa là phần màn hình của nó sẽ KHÔNG BAO GIỜ tới: dự án không có danh mục nào,
/// hoặc không cần báo cáo nào. Bắt bảng màn hình chờ một nhóm như thế là xoá nó khỏi buổi phỏng vấn — mà
/// <see cref="PermissionMatrixGate"/> lại coi <c>[KHÔNG ÁP DỤNG]</c> là đã trả lời và cứ thế mở, nên hậu
/// quả không phải một chỗ kẹt thấy được mà là bảng phân quyền quay về đứng trên các dòng CHƯA AI DUYỆT của
/// bảng này, đúng cái nền mà bảng này sinh ra để vá. Ngược lại, nhóm còn
/// <c>[MỘT PHẦN]</c>/<c>[CHƯA HỎI]</c> nghĩa là phần ấy vẫn đang trên đường ⇒ chờ; và chờ ở đó không dựng
/// thêm chỗ kẹt nào vì <see cref="PermissionMatrixGate"/> vốn đã đòi mọi nhóm áp dụng ngã ngũ mới mở. Nhóm
/// báo cáo ở <c>[RÕ]</c> thì cổng báo cáo cùng mở một lượt và thắng ưu tiên ở
/// <see cref="InterviewTableGate.Select"/>, nên không có khe nào để bảng màn hình chen lên trước.
/// </para>
///
/// <para>
/// <b>Vẫn KHÔNG đòi bảng đứng trước đã CHỐT.</b> Đó là ranh giới cố ý: điều kiện thuần bản đồ bao phủ,
/// không treo vào một artifact do model sinh ra. Model không trả nổi bảng báo cáo dùng được (structured
/// output tắt) thì cổng báo cáo cứ mở lại mỗi lượt và <see cref="InterviewTableGate.Select"/> giữ nguyên
/// quyền ưu tiên của nó — thêm một dây trói vào <c>ReportMap != null</c> ở đây chỉ đổi một lượt hỏng thành
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
/// <b>Cổng DUY NHẤT mở lại được sau khi đã chốt</b> — vì nó là cổng duy nhất mà phạm vi có thể trôi tiếp
/// sau lượt chốt. Từ khi các nguồn màn hình được đưa lên TRƯỚC bảng này, đường mở lại
/// không còn gánh phần trôi biết-trước-là-sẽ-xảy-ra (danh mục và báo cáo nay có mặt ngay ở lần bày đầu); nó
/// lùi về đúng vai lưới an toàn cho phần trôi THẬT: một màn hình — hoặc một CHỨC NĂNG trên màn hình đã chốt
/// — lộ ra từ hội thoại sau lượt chốt. Ca thật (dự án Learning and Development 7): bảng chốt ở lượt 23; tới
/// lượt 33 người dùng nói sĩ số tối thiểu/tối đa lấy từ *"danh sách khóa học được quản lý ở một màn hình
/// riêng"*, và Admin đã được chốt là người quản lý cả phòng học lẫn người dạy. Ba màn hình đó có mặt trong
/// phạm vi nhưng không bao giờ đi qua bảng: <see cref="ScreenScopeMapBuilder.EffectiveScreens"/> đưa chúng
/// thẳng vào bảng phân quyền ở dạng TRẮNG (không việc, không chức năng, không bước luồng), trong khi khối
/// ngữ cảnh của bảng đã chốt CẤM BA hỏi lại việc của từng màn — nên chúng đi vào tài liệu và vào bản demo
/// mà không ai biết chúng để làm gì.
/// </para>
///
/// <para>
/// Mở lại KHÔNG phải rà lại từ đầu: lượt bày lại được gieo bằng
/// <see cref="ScreenScopeMapBuilder.SeedRows"/> nên các dòng người dùng đã duyệt giữ nguyên việc, chức
/// năng và ô "phục vụ bước nào"; phần tươi chỉ là các mục còn chờ duyệt. Vòng lặp có đáy vì đường GỬI đóng
/// dấu <see cref="ScreenScopeRow.ConfirmedByUser"/> cho MỌI dòng và MỌI chức năng của bảng vừa bày — giữ
/// hay bỏ tích đều là đã rà — nên sau một lượt gửi thì không còn mục chờ duyệt nào và cổng đóng.
/// </para>
/// </summary>
public static class ScreenScopeGate
{
    /// <summary>Đã tới lúc bày (hoặc bày LẠI) bảng màn hình cho dự án này chưa.</summary>
    public static bool ShouldAsk(Project project)
        => ShouldAsk(project.RequirementCoverageMap, project.ScreenScopeMap);

    /// <summary>Bản thuần dữ liệu — để test và để gọi từ nơi không có entity.</summary>
    public static bool ShouldAsk(string? coverageMap, string? screenScopeJson)
    {
        // Không còn mục nào chờ duyệt ⇒ ĐÓNG. Bảng chưa có dòng nào thì không có gì để hỏi; bảng đã được
        // rà trọn thì nó chính là câu trả lời của người dùng, và bày lại một bảng y hệt là bắt họ làm lại
        // việc vừa làm.
        if (!ScreenScopeMapBuilder.HasPending(screenScopeJson))
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        // LẦN BÀY ĐẦU nhường mọi nguồn màn hình đứng trước: chờ tới khi mỗi nguồn ĐÃ NGÃ NGŨ — hoặc đủ
        // điều kiện mở (cùng mở một lượt, rồi thứ tự ưu tiên ở Select phân xử), hoặc sẽ không bao giờ mở vì
        // nhóm của nó đứng ở [KHÔNG ÁP DỤNG]. Chờ "sẵn sàng" thay vì "ngã ngũ" là để một dự án không có
        // danh mục/không có báo cáo mất hẳn bảng màn hình. Xem phần đầu class cho ca thật.
        if (!ScreenScopeMapBuilder.IsConfirmed(screenScopeJson))
            return FirstShowReady(items);

        // ĐƯỜNG MỞ LẠI giữ nguyên điều kiện cũ, và đây không phải sơ hở: tới đây thì bảng đã chốt một lần,
        // tức mọi điều kiện của lần bày đầu ĐÃ từng đúng, và việc của lượt này chỉ là mấy mục vừa lộ ra.
        // Đòi lại cả bộ là để một nhóm bị lượt distill hạ xuống [MỘT PHẦN] chặn mất đường thu hồi phần
        // phạm vi trôi — mà chính nó mới là lý do cổng này được phép mở lại.
        return InterviewTableGate.IsClear(items, InterviewTableGate.Groups.MainFlow);
    }

    /// <summary>
    /// Điều kiện bản đồ cho LẦN BÀY ĐẦU của bảng này — tách riêng vì <see cref="InterviewScopeService"/>
    /// phải đọc lại đúng nó (lượt chắt lọc phạm vi chạy theo cùng nhịp). Bao
    /// <see cref="FlowMapGate.CoverageReady"/>, cộng hai nhóm nguồn màn hình đã ngã ngũ: «Dữ liệu / danh mục
    /// chính» (màn hình quản lý danh mục) và «Báo cáo / thống kê» (màn hình báo cáo).
    /// </summary>
    internal static bool FirstShowReady(IReadOnlyList<CoverageMapItem> items)
        => FlowMapGate.CoverageReady(items)
           && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.Data)
           && InterviewTableGate.IsSettled(items, InterviewTableGate.Groups.Report);

    /// <summary>
    /// Hai bảng đứng TRƯỚC bảng màn hình (luồng, báo cáo) đã hết việc chưa: không cổng nào trong số đó còn
    /// đòi một lượt bày. "Hết việc" gồm cả hai đường — bảng đã được người dùng CHỐT, hoặc cổng của nó sẽ
    /// không bao giờ mở (nhóm «Báo cáo / thống kê» không lên <c>[RÕ]</c>).
    ///
    /// <para>
    /// <b>Chính cổng này KHÔNG dùng nó, và đó là cố ý.</b> Cổng bày bảng có
    /// <see cref="InterviewTableGate.Select"/> đứng trên phân xử: mở sớm thì bảng đứng trước thắng ưu tiên
    /// và lượt ấy vẫn bày đúng bảng cần bày, không mất gì. Thứ KHÔNG có trọng tài là
    /// <see cref="InterviewScopeService"/> — nó chạy ngay khi điều kiện của nó đúng, và cái nó sinh ra thì
    /// ở lại vĩnh viễn (<see cref="ScreenScopeMapBuilder.Merge"/> chỉ được THÊM). Nên vế này tồn tại cho
    /// riêng lượt chắt lọc, đặt ở đây để nó nằm cạnh các điều kiện nó phải khớp.
    /// </para>
    ///
    /// <para>
    /// Không dựng thêm chỗ kẹt: một cổng đứng trước kẹt MỞ (model không trả nổi bảng dùng được, người dùng
    /// không gửi) thì <see cref="InterviewTableGate.Select"/> vốn đã không bao giờ chọn tới bảng màn hình,
    /// nên phần phạm vi chắt ra lúc ấy cũng không có đường nào để đi ra hỏi.
    /// </para>
    /// </summary>
    internal static bool PrecedingTablesDone(string? coverageMap, string? flowMapJson, string? reportMapJson)
        => !FlowMapGate.ShouldAsk(coverageMap, flowMapJson)
           && !ReportMapGate.ShouldAsk(coverageMap, reportMapJson);
}

/// <summary>
/// Cổng TẤT ĐỊNH cho BẢNG BÁO CÁO / THỐNG KÊ — bảng THỨ HAI, đứng giữa bảng luồng và bảng màn hình.
///
/// <para>
/// Mỗi báo cáo còn giữ lại là một MÀN HÌNH gieo vào bảng màn hình
/// (<see cref="ReportMapBuilder.ReportScreens(string?)"/>) nên bảng này phải đứng TRƯỚC bảng màn hình — hỏi
/// sau thì các màn hình báo cáo không có mặt ở lần bày đầu của bảng màn hình, và người dùng vừa chốt xong
/// một phạm vi "trọn vẹn" đã thấy nó thay đổi. Xem ghi chú của <see cref="InterviewTableGate"/>.
/// </para>
///
/// <para>
/// Điều kiện mở, cả ba đều bắt buộc:
/// </para>
/// <list type="number">
///   <item><b>Chưa chốt bảng.</b></item>
///   <item><b>Cổng luồng đã ĐỦ ĐIỀU KIỆN MỞ</b> (<see cref="FlowMapGate.CoverageReady"/>) — cùng luật
///   nhường-thứ-tự mà <see cref="ScreenScopeGate"/> áp: thiếu vế này thì một nhóm báo cáo lên <c>[RÕ]</c>
///   sớm (người dùng kể nhu cầu báo cáo ngay lượt đầu) đẩy bảng báo cáo lên trước bảng luồng.</item>
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
        => ShouldAsk(project.RequirementCoverageMap, project.ReportMap);

    /// <summary>Bản thuần dữ liệu — để test và để gọi từ nơi không có entity.</summary>
    public static bool ShouldAsk(string? coverageMap, string? reportMapJson)
    {
        if (ReportMapBuilder.IsConfirmed(reportMapJson))
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        return FlowMapGate.CoverageReady(items)
               && InterviewTableGate.IsClear(items, InterviewTableGate.Groups.Report);
    }
}

/// <summary>
/// Cổng TẤT ĐỊNH cho BẢNG THÔNG BÁO / NHẮC NHỞ — bảng THỨ SÁU và là bảng cuối cùng của buổi phỏng vấn.
///
/// <para>
/// Nhóm «Thông báo / nhắc nhở» là nhóm THỨ HAI không được hỏi bằng câu hỏi, và vì đúng lý do của nhóm phân
/// quyền. Chuẩn <c>[RÕ]</c> của nó đòi hai vế GHÉP ĐƯỢC với nhau — mỗi loại sự kiện biết ai là người nhận
/// của RIÊNG nó — trong khi hình dạng tự nhiên của câu hỏi lại tách chúng ra làm hai câu rời ("vai trò nào
/// cần nhận email?" + "sự kiện nào cần gửi?"). Ca thật đã ghi ở <c>requirement-coverage.v5.md</c>: người
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
///   <item><b>Nhóm «Vòng đời &amp; trạng thái» đã <c>[RÕ]</c></b> (<see cref="HasLifecycle"/>) — các DÒNG
///   của bảng là những trạng thái mà hội thoại đã gọi tên, model ráp lại từ chính lời người dùng. Nhóm ở
///   <c>[KHÔNG ÁP DỤNG]</c> nghĩa là không có trạng thái nào, tức không có sự kiện nào để hỏi, và bảng này
///   KHÔNG bao giờ được bày: nhóm quay về đường hỏi bằng câu hỏi (xem lệnh cấm hỏi lẻ trong
///   <c>BAChatService</c>, nó tự tắt đúng ở ca này). Thiếu đường thoát đó thì ứng dụng danh mục thuần —
///   không đối tượng nào có trạng thái — kẹt vĩnh viễn: không bảng nào bày ra, không câu hỏi nào được
///   phép, nhóm không bao giờ <c>[RÕ]</c>, nút "Write Requirement" không bao giờ sáng.</item>
/// </list>
/// </summary>
public static class NotificationMapGate
{
    /// <summary>
    /// Nhãn nhóm thông báo trong bản đồ bao phủ — khớp <c>requirement-coverage.v5.md</c> và mục 12 nhãn
    /// nhóm của <c>requirement-chat.v4.md</c>. Cùng vai trò với
    /// <see cref="PermissionMatrixGate.PermissionGroupLabel"/>: hai nhóm chốt-bằng-bảng đều cần một chỗ
    /// duy nhất giữ nhãn, vì <see cref="CoverageConfirmedTableGuard"/> phải tìm đúng dòng để viết lại.
    /// </summary>
    public const string NotificationGroupLabel = "Thông báo / nhắc nhở";

    /// <summary>Đã tới lúc bày bảng thông báo cho dự án này chưa.</summary>
    public static bool ShouldAsk(Project project)
        => ShouldAsk(project.NotificationMap, project.PermissionMatrix, project.RequirementCoverageMap);

    /// <summary>Bản thuần dữ liệu — để test và để gọi từ nơi không có entity.</summary>
    public static bool ShouldAsk(string? notificationMapJson, string? permissionMatrixJson, string? coverageMap)
    {
        if (NotificationMapBuilder.IsConfirmed(notificationMapJson))
            return false;
        if (!PermissionMatrixGate.IsConfirmed(permissionMatrixJson))
            return false;

        return HasLifecycle(coverageMap);
    }

    /// <summary>
    /// Dự án có vòng đời trạng thái để ráp thành các dòng sự kiện không: nhóm «Vòng đời &amp; trạng thái» đã
    /// <c>[RÕ]</c>. Tới lúc bảng phân quyền đã chốt thì mọi nhóm áp dụng đều đã <c>[RÕ]</c>, nên ở đây nhóm
    /// chỉ còn hai khả năng thật: <c>[RÕ]</c> (có trạng thái ⇒ bày bảng) hoặc <c>[KHÔNG ÁP DỤNG]</c> (ứng
    /// dụng danh mục thuần ⇒ không bao giờ bày). Dùng chung với lệnh cấm hỏi lẻ ở <c>BAChatService</c> —
    /// hai chỗ phải khớp nhau, lệch nhau là cấm hỏi trong khi bảng không bao giờ tới.
    /// </summary>
    public static bool HasLifecycle(string? coverageMap)
        => InterviewTableGate.IsClear(CoverageMapParser.Parse(coverageMap), InterviewTableGate.Groups.Lifecycle);
}
