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
/// (<see cref="ReportMapDraftService"/>, <see cref="UxDesignService"/>,
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
/// Luồng đứng ĐẦU vì mọi thứ sau đều trỏ về bước luồng: mỗi tác vụ của bản thiết kế UX có ô "phục vụ bước
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
/// <see cref="FlowMapGate.CoverageReady"/>, lần thiết kế đầu của <see cref="UxDesignGate"/> mượn
/// <see cref="UxDesignGate.FirstDraftReady"/> (đã bao điều kiện của cổng luồng). Hai ranh giới cố ý: chỉ
/// mượn ĐIỀU KIỆN BẢN ĐỒ (cộng đúng một vế "bảng đứng trước đã hết việc" ở
/// <see cref="UxDesignGate.PrecedingTablesDone"/>); và bước sau chờ bước trước NGÃ NGŨ chứ không chờ nó
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
    /// lại đúng nó (và <see cref="UxDesignGate"/> đọc gián tiếp qua
    /// <see cref="UxDesignGate.FirstDraftReady"/>): bước đứng sau chỉ được chạy LẦN ĐẦU khi cổng luồng đã
    /// đủ điều kiện mở. Hai nơi cùng một điều kiện mà chép tay hai bản thì lần sửa sau chỉ sửa một bản, và
    /// cái sai quay lại đúng hình dạng cũ — một bảng đi trước bảng luồng.
    /// </summary>
    internal static bool CoverageReady(IReadOnlyList<CoverageMapItem> items)
        => InterviewTableGate.IsClear(items, CoverageGroupKey.MainFlow)
           && InterviewTableGate.IsClear(items, CoverageGroupKey.Roles)
           && InterviewTableGate.IsSettled(items, CoverageGroupKey.Exceptions);
}

/// <summary>
/// Cổng TẤT ĐỊNH của lượt THIẾT KẾ UX (<see cref="UxDesignService"/>) — thay cổng của bảng màn hình cũ.
///
/// <para>
/// <b>Lần thiết kế ĐẦU chờ mọi nguồn tác vụ NGÃ NGŨ</b> (<see cref="FirstDraftReady"/>): cổng luồng đủ điều
/// kiện mở, «Dữ liệu / danh mục chính» và «Báo cáo / thống kê» đã được CHẠM TỚI (<c>[RÕ]</c> hoặc
/// <c>[KHÔNG ÁP DỤNG]</c>) — cộng bảng luồng và bảng báo cáo đã hết việc (<see cref="PrecedingTablesDone"/>).
/// Thiết kế trước khi luồng chốt là thiết kế trên phỏng đoán: ô "tác vụ này phục vụ bước nào" ra rỗng và gần
/// như mọi bước thành mồ côi (ca thật JD Library 1, thời bảng màn hình còn chạy sớm).
/// </para>
///
/// <para>
/// <b>Đường THIẾT KẾ LẠI</b> mở khi phạm vi TRÔI thật (<see cref="NeedsRedesign"/>): lượt chắt lọc vừa thêm tác
/// vụ chưa xếp chỗ, người dùng sửa bảng luồng làm lộ ra bước chưa có tác vụ, hoặc bảng báo cáo đổi. Chỉ đòi
/// luồng chính <c>[RÕ]</c> — tới đây mọi điều kiện của lần đầu đã từng đúng, đòi lại cả bộ là để một nhóm bị
/// distill hạ xuống <c>[MỘT PHẦN]</c> chặn mất phần phạm vi trôi. Vòng lặp có đáy vì mỗi lượt thiết kế xếp chỗ
/// MỌI tác vụ (<see cref="UxBlueprintBuilder.Repair"/>) và phủ MỌI bước luồng (lưới an toàn của danh mục).
/// </para>
/// </summary>
public static class UxDesignGate
{
    public static bool ShouldDesign(Project project)
        => PrecedingTablesDone(project.RequirementCoverageMap, project.FlowMap, project.ReportMap)
           && ShouldDesign(project.RequirementCoverageMap, project.UxBlueprint, project.FlowMap, project.ReportMap);

    public static bool ShouldDesign(string? coverageMap, string? blueprintJson, string? flowMapJson, string? reportMapJson)
    {
        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
            return false;

        if (!UxBlueprintBuilder.IsDesigned(blueprintJson))
            return FirstDraftReady(items);

        return InterviewTableGate.IsClear(items, CoverageGroupKey.MainFlow)
               && NeedsRedesign(blueprintJson, flowMapJson, reportMapJson);
    }

    /// <summary>Phạm vi đã trôi so với bản thiết kế đang lưu chưa.</summary>
    public static bool NeedsRedesign(string? blueprintJson, string? flowMapJson, string? reportMapJson)
    {
        var bp = UxBlueprintBuilder.Parse(blueprintJson);
        if (bp.Tasks.Any(t => !t.Placed))
            return true;
        if (UxInventoryBuilder.UncoveredSteps(bp.Tasks, flowMapJson).Count > 0)
            return true;

        var wanted = UxInventoryBuilder.ReportTasks(reportMapJson).Select(t => RequirementText.Normalize(t.Name)).ToHashSet();
        var have = bp.Tasks.Where(t => t.Kind == UxTaskKind.Report).Select(t => RequirementText.Normalize(t.Name)).ToHashSet();
        return !wanted.SetEquals(have);
    }

    /// <summary>
    /// Chế độ của lượt: sau khi người dùng đã DUYỆT một Product Brief (liệt kê các màn của bản thiết kế) thì
    /// chỉ PATCH — giữ cấu trúc, xếp chỗ phần mới; trước đó thì thiết kế tự do.
    /// </summary>
    public static string Mode(Project project)
        => project.BriefApprovedTurnCount > 0 && UxBlueprintBuilder.IsDesigned(project.UxBlueprint)
            ? UxDesignMode.Patch
            : UxDesignMode.Design;

    internal static bool FirstDraftReady(IReadOnlyList<CoverageMapItem> items)
        => FlowMapGate.CoverageReady(items)
           && InterviewTableGate.IsSettled(items, CoverageGroupKey.Data)
           && InterviewTableGate.IsSettled(items, CoverageGroupKey.Reports);

    /// <summary>
    /// Bảng luồng và bảng báo cáo — hai nguồn tác vụ đứng trước — đã HẾT VIỆC: không còn cổng nào của chúng đang
    /// chờ mở. Lượt chắt lọc phạm vi dùng chung vế này.
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
/// Mỗi báo cáo là một TÁC VỤ của bản thiết kế UX (<see cref="UxInventoryBuilder.ReportTasks"/>), kèm chỗ đứng
/// bảng này đã chốt (khối của màn tổng quan, ô đếm, hay màn riêng) — nên bảng này phải có mặt TRƯỚC lần thiết
/// kế đầu (<see cref="UxDesignGate.PrecedingTablesDone"/> chờ nó).
/// </para>
///
/// <para>
/// Điều kiện mở, cả ba đều bắt buộc:
/// </para>
/// <list type="number">
///   <item><b>Chưa có bảng.</b></item>
///   <item><b>Cổng luồng đã ĐỦ ĐIỀU KIỆN MỞ</b> (<see cref="FlowMapGate.CoverageReady"/>) — cùng luật
///   mượn-điều-kiện mà <see cref="UxDesignGate"/> áp: thiếu vế này thì một nhóm báo cáo lên <c>[RÕ]</c>
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
