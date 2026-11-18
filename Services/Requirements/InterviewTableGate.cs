using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>Bảng mà một lượt chat được phép bày ra. <see cref="None"/> = lượt chat thường.</summary>
public enum InterviewTableKind
{
    /// <summary>Không bảng nào — lượt hỏi/trả lời bình thường.</summary>
    None,

    /// <summary>Bảng LUỒNG nghiệp vụ theo vai trò (luồng chính + ngoại lệ).</summary>
    FlowMap
}

/// <summary>
/// Chọn ĐÚNG MỘT bảng cho một lượt chat, tất định, từ chính bản đồ bao phủ và các bảng đã chốt.
///
/// <para>
/// <b>Vì sao phải có một chỗ chọn duy nhất.</b> Mỗi cổng bơm một khối <c>## LƯỢT NÀY:</c> vào ngữ cảnh, và
/// hai khối như thế cùng lúc là hai mệnh lệnh chọi nhau — model sẽ trả một bảng lai hoặc bỏ cả hai.
/// </para>
///
/// <para>
/// <b>Chỉ còn MỘT bảng bày ra cho người dùng chốt: bảng luồng.</b> Ba bảng còn lại của buổi phỏng vấn —
/// báo cáo, màn hình, phân quyền — là phần THIẾT KẾ của BA, nên BA tự soạn chúng ngầm ở ĐẦU lượt
/// (<see cref="ReportMapDraftService"/>, <see cref="ScreenScopeDraftService"/>,
/// <see cref="PermissionMatrixDraftService"/>) theo đúng thứ tự phụ thuộc
/// <c>luồng → báo cáo → màn hình → phân quyền</c>, TRƯỚC lời gọi <see cref="Select"/>. Người dùng cung cấp
/// yêu cầu (ai làm gì, khi nào, trạng thái nào) qua hội thoại và bảng luồng; cần bao nhiêu màn hình, nút
/// nào đặt ở màn nào, quyền CRUD của từng vai trên từng chức năng là BA tự quyết. Bảng phân quyền vẫn
/// xem/sửa được — ở popup mở từ dòng «Phân quyền theo nghiệp vụ» của panel tiến độ — nhưng không còn là
/// một lượt bắt người dùng rà trước khi đi tiếp.
/// </para>
///
/// <para>
/// <b>Không có bảng thông báo.</b> Sản phẩm của buổi phỏng vấn là một POC chạy trên trình duyệt, không gửi
/// email thật, nên bắt người dùng chọn To/CC cho từng sự kiện là đòi một quyết định họ chưa thấy tác dụng —
/// ở đúng lúc mệt nhất của buổi. Nhóm «Thông báo / nhắc nhở» chỉ GHI NHẬN điều người dùng tự kể, không hỏi,
/// không chặn (<see cref="CoverageConfirmedTableGuard"/>), và người nhận email được ghi rõ là CHƯA CHỐT cho
/// đội Dev xác nhận sau POC.
/// </para>
///
/// <para>
/// Luồng đứng ĐẦU vì mọi thứ sau đều trỏ về bước luồng: bảng màn hình có ô "chức năng này phục vụ bước
/// nào".
/// </para>
///
/// <para>
/// <b>Không có bảng đối tượng nghiệp vụ.</b> Mô hình dữ liệu (đối tượng nào có thông tin nào, bắt buộc hay
/// không, chọn từ danh sách nào) KHÔNG được chốt với người dùng nghiệp vụ: họ không rà nổi những ô đó, và
/// bấm gửi một bảng mình không hiểu là ký tên vào nó.
/// </para>
///
/// <para>
/// <b>Vì sao bảng luồng, bảng báo cáo và bảng màn hình không được là điều kiện để một nhóm lên
/// <c>[RÕ]</c>.</b> Nhóm «Phân quyền theo nghiệp vụ» có luật khắt khe một chiều — chưa có bảng thì không
/// bao giờ <c>[RÕ]</c> — và luật đó đúng vì nó KHÔNG được hỏi bằng câu hỏi. Ba nhóm kia thì có: chúng
/// được hỏi suốt buổi, và bảng chỉ XÁC NHẬN LẠI (luồng) hoặc RÁP LẠI (báo cáo, màn hình) thứ hội thoại đã
/// trả lời. Áp luật một chiều cho chúng là dựng một vòng khóa kín —
/// cổng đòi nhóm <c>[RÕ]</c> mới mở, bản đồ đòi có bảng mới <c>[RÕ]</c>, và không bên nào đi trước được.
/// Đó chính là cái bẫy mà <see cref="PermissionMatrixGate"/> né bằng cách cố ý BỎ QUA dòng của chính nó
/// (và dòng thông báo) khi xét.
/// </para>
///
/// <para>
/// <b>Mỗi bước sau MƯỢN điều kiện bản đồ của bước trước</b>: <see cref="ReportMapGate"/> mượn
/// <see cref="FlowMapGate.CoverageReady"/>, lần soạn đầu của <see cref="ScreenScopeGate"/> mượn
/// <see cref="ScreenScopeGate.FirstDraftReady"/> (đã bao điều kiện của cổng luồng). Hai ranh giới cố ý: chỉ
/// mượn ĐIỀU KIỆN BẢN ĐỒ (cộng đúng một vế "bảng đứng trước đã hết việc" ở
/// <see cref="ScreenScopeGate.PrecedingTablesDone"/>); và bước sau chờ bước trước NGÃ NGŨ chứ không chờ nó
/// SẴN SÀNG — một nhóm ở <c>[KHÔNG ÁP DỤNG]</c> nghĩa là bước đứng trước sẽ không bao giờ tới, và chờ nó là
/// xoá luôn bước đứng sau khỏi buổi phỏng vấn.
/// </para>
/// </summary>
public static class InterviewTableGate
{
    /// <summary>Bảng của lượt này.</summary>
    public static InterviewTableKind Select(Project project)
    {
        // Báo cáo, màn hình, phân quyền không có ở đây: BA đã soạn ngầm chúng ở đầu lượt
        // (BAChatService.BuildTurnContextAsync). Thông báo cũng không: nhóm đó không chốt ở buổi phỏng vấn
        // (xem CoverageConfirmedTableGuard.NotificationDeferredNote).
        return FlowMapGate.ShouldAsk(project) ? InterviewTableKind.FlowMap : InterviewTableKind.None;
    }

    /// <summary>
    /// Nhóm mang khoá <paramref name="key"/> (<see cref="CoverageGroupKey"/>) đã ở trạng thái <c>[RÕ]</c>
    /// chưa. Nhận ra nhóm bằng KHOÁ chứ không bằng nhãn: nhãn là chữ người dùng đọc, đổi theo ngôn ngữ.
    /// Nhóm không có mặt trong bản đồ ⇒ false (fail-closed): bản đồ thiếu dòng là bản đồ hỏng, không phải
    /// một nhóm đã xong.
    /// </summary>
    internal static bool IsClear(IReadOnlyList<CoverageMapItem> items, string key)
        => Find(items, key)?.Status == CoverageStatus.Clear;

    /// <summary>
    /// Nhóm đã được CHẠM TỚI: <c>[RÕ]</c> hoặc <c>[KHÔNG ÁP DỤNG]</c>. Dùng cho các nhóm mà bảng chỉ chở
    /// một phần (ngoại lệ, vòng đời) — đòi <c>[RÕ]</c> ở đó là hoãn cổng tới tận lúc cổng phân quyền mở,
    /// tức dồn mọi bảng vào cuối buổi, đúng thứ thiết kế này muốn tránh.
    /// </summary>
    internal static bool IsSettled(IReadOnlyList<CoverageMapItem> items, string key)
    {
        var status = Find(items, key)?.Status;
        return status is CoverageStatus.Clear or CoverageStatus.NotApplicable;
    }

    private static CoverageMapItem? Find(IReadOnlyList<CoverageMapItem> items, string key)
        => items.FirstOrDefault(x => x.Key == key);
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
    /// <see cref="ScreenScopeGate.FirstDraftReady"/>): bước đứng sau chỉ được chạy LẦN ĐẦU khi cổng luồng đã
    /// đủ điều kiện mở. Hai nơi cùng một điều kiện mà chép tay hai bản thì lần sửa sau chỉ sửa một bản, và
    /// cái sai quay lại đúng hình dạng cũ — một bảng đi trước bảng luồng.
    /// </summary>
    internal static bool CoverageReady(IReadOnlyList<CoverageMapItem> items)
        => InterviewTableGate.IsClear(items, CoverageGroupKey.MainFlow)
           && InterviewTableGate.IsClear(items, CoverageGroupKey.Roles)
           && InterviewTableGate.IsSettled(items, CoverageGroupKey.Exceptions);
}

/// <summary>
/// Cổng TẤT ĐỊNH cho việc SOẠN PHẠM VI MÀN HÌNH — không phải cổng bày bảng: phạm vi màn hình là phần thiết
/// kế của BA, nên <see cref="ScreenScopeDraftService"/> soạn và chốt nó ngầm ở đầu lượt, người dùng không rà.
///
/// <para>
/// <b>Vì sao người dùng không rà bảng này nữa.</b> Việc của người dùng là cung cấp yêu cầu: ai làm gì, khi
/// nào, trạng thái nào — và họ đã làm việc đó ở hội thoại và ở bảng luồng. Cần bao nhiêu màn hình, chức
/// năng nào đặt trên màn nào, nút nào nằm ở đâu là thiết kế, đúng phần họ đi thuê BA để làm. Bảng màn hình
/// bày ra cho họ chốt (và các câu kiểu *"nút Thu hồi tủ nên đặt ở màn hình nào?"* mà nó kéo theo khi phạm
/// vi trôi sau lúc chốt) là đẩy ngược phần đó về cho người đi thuê. Chỗ người dùng góp ý về màn hình là
/// POC Review — nơi họ thấy màn hình thật chứ không phải một bảng chữ.
/// </para>
///
/// <para>
/// Bước soạn phải đứng sau mọi NGUỒN màn hình: bảng báo cáo (màn tổng quan gom các báo cáo, cộng màn riêng
/// của báo cáo nào cần) gieo thẳng vào các DÒNG của bảng này lúc được ráp, còn màn hình quản lý danh mục do
/// lượt chắt lọc phạm vi bắt từ hội thoại về nhóm «Dữ liệu / danh mục chính». Và phải đứng sau bảng luồng
/// ĐÃ CHỐT, vì ô "chức năng này phục vụ bước nào" trỏ vào chính các bước ấy.
/// </para>
///
/// <para>
/// Điều kiện mở:
/// </para>
/// <list type="number">
///   <item><b>Bảng còn mục CHƯA CHỐT</b> (<see cref="ScreenScopeMapBuilder.HasPending"/>) — một dòng, hoặc
///   một chức năng trên một dòng đã chốt, mà BA chưa soạn xong. Bảng trống thì không có gì để soạn, bảng đã
///   chốt trọn thì không còn gì để soạn lại.</item>
///   <item><b>Các bảng đứng trước đã hết việc</b> (<see cref="PrecedingTablesDone"/>): bảng luồng đã chốt
///   (hoặc cổng của nó không bao giờ mở), bảng báo cáo đã ráp (hoặc nhóm báo cáo không lên <c>[RÕ]</c>).</item>
///   <item><b>Lần soạn ĐẦU TIÊN: mọi nguồn màn hình đã NGÃ NGŨ</b> — <see cref="FirstDraftReady"/>: cổng
///   luồng đã đủ điều kiện mở, «Dữ liệu / danh mục chính» và «Báo cáo / thống kê» đã được CHẠM TỚI. Soạn
///   sớm hơn thì ô "phục vụ bước nào" ra rỗng vì chưa có bước nào để gắn. Đường SOẠN LẠI (phạm vi trôi sau
///   lần chốt) chỉ đòi «Chức năng &amp; luồng nghiệp vụ chính» <c>[RÕ]</c>.</item>
/// </list>
///
/// <para>
/// <b>Chờ "NGÃ NGŨ", không chờ "SẴN SÀNG".</b> Một nhóm ở <c>[KHÔNG ÁP DỤNG]</c> nghĩa là phần màn hình của
/// nó sẽ KHÔNG BAO GIỜ tới: dự án không có danh mục nào, hoặc không cần báo cáo nào. Chờ một nhóm như thế là
/// không bao giờ soạn phạm vi — và bảng phân quyền đứng trên phạm vi đó cũng không bao giờ tới.
/// </para>
///
/// <para>
/// <b>Cổng DUY NHẤT mở lại được sau khi đã chốt</b> — vì phạm vi là thứ duy nhất trôi tiếp được sau lượt
/// chốt: một màn hình hoặc một CHỨC NĂNG mới lộ ra từ hội thoại (người dùng thêm một trạng thái, một thao
/// tác). Lượt chắt lọc phạm vi (<see cref="InterviewScopeService"/>) ghép chúng vào bảng ở trạng thái chưa
/// chốt, cổng này mở, và lượt soạn kế tiếp chỉ mô tả + xếp chỗ phần mới — các dòng đã chốt giữ nguyên
/// (<see cref="ScreenScopeMapBuilder.SeedRows"/>). Vòng lặp có đáy vì lượt soạn đóng dấu chốt cho MỌI dòng
/// và MỌI chức năng của bảng vừa soạn.
/// </para>
/// </summary>
public static class ScreenScopeGate
{
    /// <summary>Đã tới lúc soạn (hoặc soạn LẠI) phạm vi màn hình cho dự án này chưa.</summary>
    public static bool ShouldDraft(Project project)
        => PrecedingTablesDone(project.RequirementCoverageMap, project.FlowMap, project.ReportMap)
           && ShouldDraft(project.RequirementCoverageMap, project.ScreenScopeMap);

    /// <summary>
    /// Phần điều kiện đọc trên bản đồ + bảng màn hình — để test và để gọi từ nơi không có entity. KHÔNG gồm
    /// vế <see cref="PrecedingTablesDone"/>; bản nhận <see cref="Project"/> mới là điều kiện đầy đủ.
    /// </summary>
    public static bool ShouldDraft(string? coverageMap, string? screenScopeJson)
    {
        // Không còn mục nào chưa chốt ⇒ ĐÓNG. Bảng chưa có dòng nào thì không có gì để soạn; bảng đã chốt
        // trọn thì soạn lại một bảng y hệt là một lời gọi LLM cho không.
        if (!ScreenScopeMapBuilder.HasPending(screenScopeJson))
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        // LẦN SOẠN ĐẦU chờ mọi nguồn màn hình NGÃ NGŨ — xem phần đầu class.
        if (!ScreenScopeMapBuilder.IsConfirmed(screenScopeJson))
            return FirstDraftReady(items);

        // ĐƯỜNG SOẠN LẠI chỉ đòi luồng chính [RÕ]: tới đây thì mọi điều kiện của lần soạn đầu ĐÃ từng đúng,
        // và đòi lại cả bộ là để một nhóm bị lượt distill hạ xuống [MỘT PHẦN] chặn mất phần phạm vi trôi.
        return InterviewTableGate.IsClear(items, CoverageGroupKey.MainFlow);
    }

    /// <summary>
    /// Điều kiện bản đồ cho LẦN SOẠN ĐẦU — tách riêng vì <see cref="InterviewScopeService"/> phải đọc lại
    /// đúng nó (lượt chắt lọc phạm vi chạy theo cùng nhịp). Bao <see cref="FlowMapGate.CoverageReady"/>, cộng
    /// hai nhóm nguồn màn hình đã ngã ngũ: «Dữ liệu / danh mục chính» (màn hình quản lý danh mục) và «Báo
    /// cáo / thống kê» (màn hình báo cáo).
    /// </summary>
    internal static bool FirstDraftReady(IReadOnlyList<CoverageMapItem> items)
        => FlowMapGate.CoverageReady(items)
           && InterviewTableGate.IsSettled(items, CoverageGroupKey.Data)
           && InterviewTableGate.IsSettled(items, CoverageGroupKey.Reports);

    /// <summary>
    /// Hai bảng đứng TRƯỚC phạm vi màn hình (luồng, báo cáo) đã hết việc chưa: không cổng nào trong số đó còn
    /// mở. "Hết việc" gồm cả hai đường — bảng đã có (bảng luồng người dùng CHỐT, bảng báo cáo BA ráp xong),
    /// hoặc cổng của nó sẽ không bao giờ mở (nhóm «Báo cáo / thống kê» không lên <c>[RÕ]</c>).
    ///
    /// <para>
    /// Dùng ở HAI chỗ không có trọng tài nào đứng trên: lượt soạn phạm vi (<see cref="ShouldDraft(Project)"/>)
    /// và lượt chắt lọc (<see cref="InterviewScopeService"/>). Cả hai chạy ngay khi điều kiện của chúng đúng,
    /// và cái chúng sinh ra thì ở lại — soạn phạm vi trước khi bảng luồng chốt là gắn chức năng vào các bước
    /// còn chưa ai duyệt, chắt lọc quá sớm là để mấy dòng phỏng đoán nằm lại trong bảng vĩnh viễn
    /// (<see cref="ScreenScopeMapBuilder.Merge"/> chỉ được THÊM).
    /// </para>
    ///
    /// <para>
    /// Không dựng thêm chỗ kẹt nào mới: bảng luồng mà người dùng không gửi thì trước đây cũng không có đường
    /// nào tới được bảng màn hình.
    /// </para>
    /// </summary>
    internal static bool PrecedingTablesDone(string? coverageMap, string? flowMapJson, string? reportMapJson)
        => !FlowMapGate.ShouldAsk(coverageMap, flowMapJson)
           && !ReportMapGate.ShouldDraft(coverageMap, reportMapJson);
}

/// <summary>
/// Cổng TẤT ĐỊNH cho việc RÁP BẢNG BÁO CÁO / THỐNG KÊ — không phải cổng bày bảng: bảng này do
/// <see cref="ReportMapDraftService"/> ráp ngầm, người dùng không rà nó (xem ghi chú của service đó).
///
/// <para>
/// Bảng báo cáo gieo MÀN HÌNH vào bảng màn hình — màn tổng quan gom các báo cáo, và màn riêng của báo cáo
/// nào cần (<see cref="ReportMapBuilder.ReportScreenAdditions"/>) — nên bảng này phải có mặt TRƯỚC lần soạn
/// đầu của phạm vi màn hình (<see cref="ScreenScopeGate.PrecedingTablesDone"/> chờ nó).
/// </para>
///
/// <para>
/// Điều kiện mở, cả ba đều bắt buộc:
/// </para>
/// <list type="number">
///   <item><b>Chưa có bảng.</b></item>
///   <item><b>Cổng luồng đã ĐỦ ĐIỀU KIỆN MỞ</b> (<see cref="FlowMapGate.CoverageReady"/>) — cùng luật
///   mượn-điều-kiện mà <see cref="ScreenScopeGate"/> áp: thiếu vế này thì một nhóm báo cáo lên <c>[RÕ]</c>
///   sớm (người dùng kể nhu cầu báo cáo ngay lượt đầu) làm bảng được ráp — và các màn hình báo cáo được
///   gieo — khi phần còn lại của buổi phỏng vấn chưa thành hình.</item>
///   <item><b>Nhóm «Báo cáo / thống kê» đã <c>[RÕ]</c></b> — bảng chỉ RÁP LẠI thứ người dùng đã chọn. Ráp
///   sớm hơn là để model tự nghĩ ra báo cáo, và mỗi dòng nó nghĩ ra là một màn hình người dùng chưa từng
///   đặt hàng.</item>
/// </list>
///
/// <para>
/// <b>Nhóm này được hỏi bằng CÂU HỎI</b> — BA đề xuất 2–4 báo cáo dưới dạng chip, người dùng tích cái cần
/// — nên hội thoại là đường đưa nhóm lên <c>[RÕ]</c>. <c>[KHÔNG ÁP DỤNG]</c> (người dùng nói rõ không cần
/// báo cáo nào) KHÔNG phải <c>[RÕ]</c> ⇒ cổng không bao giờ mở, và không có bảng nào được ráp cho một ứng
/// dụng thuần nhập liệu.
/// </para>
/// </summary>
public static class ReportMapGate
{
    /// <summary>Đã tới lúc ráp bảng báo cáo cho dự án này chưa.</summary>
    public static bool ShouldDraft(Project project)
        => ShouldDraft(project.RequirementCoverageMap, project.ReportMap);

    /// <summary>Bản thuần dữ liệu — để test và để gọi từ nơi không có entity.</summary>
    public static bool ShouldDraft(string? coverageMap, string? reportMapJson)
    {
        if (ReportMapBuilder.IsConfirmed(reportMapJson))
            return false;

        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        return FlowMapGate.CoverageReady(items)
               && InterviewTableGate.IsClear(items, CoverageGroupKey.Reports);
    }
}
