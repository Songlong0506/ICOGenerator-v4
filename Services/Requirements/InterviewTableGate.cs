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
/// <b>Chỉ còn MỘT bảng bày ra cho người dùng chốt: bảng luồng.</b> Bảng báo cáo được BA ráp ngầm ở ĐẦU lượt
/// (<see cref="ReportMapDraftService"/>) ngay khi nhóm «Báo cáo / thống kê» <c>[RÕ]</c>. Phần THIẾT KẾ — bản
/// thiết kế UX và bảng phân quyền trên nó — không chạy trong buổi phỏng vấn mà ở bước "Write Requirement"
/// (<see cref="DesignPreparationService"/>), theo đúng thứ tự phụ thuộc <c>luồng → báo cáo → thiết kế →
/// phân quyền</c>. Người dùng cung cấp yêu cầu (ai làm gì, khi nào, trạng thái nào) qua hội thoại và bảng
/// luồng; cần bao nhiêu màn hình, nút nào đặt ở màn nào, quyền CRUD của từng vai trên từng chức năng là BA tự
/// quyết. Bản thiết kế và bảng phân quyền xem/sửa được ở hai popup dưới panel tiến độ.
/// </para>
///
/// <para>
/// <b>Không có bảng thông báo.</b> Sản phẩm của buổi phỏng vấn là một POC chạy trên trình duyệt, không gửi
/// email thật, nên bắt người dùng chọn To/CC cho từng sự kiện là đòi một quyết định họ chưa thấy tác dụng —
/// ở đúng lúc mệt nhất của buổi. Nhóm «Thông báo / nhắc nhở» chỉ GHI NHẬN điều người dùng tự kể, không hỏi,
/// không chặn (<see cref="CoverageUnaskedGroupGuard"/>), và người nhận email được ghi rõ là CHƯA CHỐT cho
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
/// <b>Vì sao bảng luồng và bảng báo cáo không được là điều kiện để một nhóm lên <c>[RÕ]</c>.</b> Hai nhóm ấy
/// được hỏi suốt buổi, và bảng chỉ XÁC NHẬN LẠI (luồng) hoặc RÁP LẠI (báo cáo) thứ hội thoại đã trả lời. Đòi
/// có bảng mới <c>[RÕ]</c> là dựng một vòng khóa kín — cổng bảng đòi nhóm <c>[RÕ]</c> mới mở, bản đồ đòi có
/// bảng mới <c>[RÕ]</c>, và không bên nào đi trước được.
/// </para>
///
/// <para>
/// <see cref="ReportMapGate"/> MƯỢN điều kiện bản đồ của cổng luồng (<see cref="FlowMapGate.CoverageReady"/>):
/// bảng báo cáo chỉ được ráp LẦN ĐẦU khi cổng luồng đã đủ điều kiện mở.
/// </para>
/// </summary>
public static class InterviewTableGate
{
    /// <summary>Bảng của lượt này.</summary>
    public static InterviewTableKind Select(Project project)
    {
        // Báo cáo không có ở đây: BA ráp ngầm nó ở đầu lượt (BAChatService.BuildTurnContextAsync). Thiết kế
        // và phân quyền cũng không: chúng chạy ở bước "Write Requirement" (DesignPreparationService). Thông báo
        // cũng không: nhóm đó không chốt ở buổi phỏng vấn (xem CoverageUnaskedGroupGuard).
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
    /// lại đúng nó: bảng báo cáo chỉ được ráp LẦN ĐẦU khi cổng luồng đã đủ điều kiện mở. Hai nơi cùng một điều kiện mà chép tay hai bản thì lần sửa sau chỉ sửa một bản, và
    /// cái sai quay lại đúng hình dạng cũ — một bảng đi trước bảng luồng.
    /// </summary>
    internal static bool CoverageReady(IReadOnlyList<CoverageMapItem> items)
        => InterviewTableGate.IsClear(items, CoverageGroupKey.MainFlow)
           && InterviewTableGate.IsClear(items, CoverageGroupKey.Roles)
           && InterviewTableGate.IsSettled(items, CoverageGroupKey.Exceptions);
}

/// <summary>
/// Cổng TẤT ĐỊNH của lượt THIẾT KẾ UX (<see cref="UxDesignService.PrepareAsync"/>). Lượt thiết kế không chạy
/// trong buổi phỏng vấn mà ở bước "Write Requirement" (<see cref="DesignPreparationService"/>), SAU cổng
/// readiness — tức khi mọi nhóm áp dụng đã <c>[RÕ]</c> và mọi nguồn tác vụ (bảng luồng, bảng báo cáo) đã ngã
/// ngũ. Cổng này vì vậy không còn hỏi "đã tới lúc chưa" mà chỉ hỏi "bản đang lưu còn đúng không".
///
/// <para>
/// <b>Bản thiết kế đang lưu CŨ</b> (<see cref="IsStale"/>) khi: chưa thiết kế lần nào; hội thoại có lượt mới
/// kể từ lần thiết kế gần nhất (<c>Project.UxDesignedTurnCount</c>) — lượt mới có thể kể thêm việc; hoặc phạm
/// vi trôi ngay trên các bảng (<see cref="NeedsRedesign"/>: bước luồng chưa tác vụ nào nhận, bộ tác vụ báo
/// cáo lệch bảng báo cáo, tác vụ chưa xếp chỗ). Không cũ ⇒ bấm lại nút không gọi model lần nào cho phần thiết
/// kế.
/// </para>
/// </summary>
public static class UxDesignGate
{
    /// <summary>Bản thiết kế đang lưu có cần dựng lại/bổ sung trước khi soạn Brief không.</summary>
    public static bool IsStale(Project project, int turnCount)
        => !UxBlueprintBuilder.IsDesigned(project.UxBlueprint)
           || turnCount > project.UxDesignedTurnCount
           || NeedsRedesign(project.UxBlueprint, project.FlowMap, project.ReportMap);

    /// <summary>Phạm vi đã trôi so với bản thiết kế đang lưu chưa — đọc thuần trên các bảng, không cần model.</summary>
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
