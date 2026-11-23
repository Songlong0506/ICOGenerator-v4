using System.Text;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Các cách KỂ LẠI bản thiết kế UX cho từng tầng đọc nó. Mỗi tầng cần một độ phân giải khác nhau, nhưng tất cả
/// đều đọc từ cùng một cột <c>Project.UxBlueprint</c> — không tầng nào có bản sao riêng để lệch.
/// </summary>
public static class UxBlueprintRenderer
{
    /// <summary>
    /// Khối ngữ cảnh cho lượt chat BA, và lượt distill bản đồ bao phủ: màn hình, việc của màn,
    /// các hành động. Nói rõ đây là THIẾT KẾ — không phải lời người dùng — để không tầng nào trích nó làm bằng
    /// chứng hay đem ra đối chất người dùng.
    /// </summary>
    public static string? RenderChatBlock(string? json)
    {
        var bp = UxBlueprintBuilder.Parse(json);
        if (bp.Screens.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bản thiết kế màn hình của ứng dụng (UX — thiết kế của BA/UiUx, KHÔNG phải lời người dùng) ---");
        sb.AppendLine("Dòng \"là gì\" và tên hành động là chữ CỦA THIẾT KẾ: đừng trích chúng làm bằng chứng, đừng lấy chúng làm một vế mâu thuẫn với điều người dùng nói.");
        foreach (var screen in bp.Screens)
        {
            sb.AppendLine($"* {screen.Name} [{screen.Pattern}]" + (screen.Audience.Count > 0 ? $" — vai: {string.Join(", ", screen.Audience)}" : string.Empty));
            if (screen.Purpose.Length > 0)
                sb.AppendLine($"  - là gì: {screen.Purpose}");
            if (screen.Actions.Count > 0)
                sb.AppendLine($"  - hành động: {string.Join("; ", screen.Actions.Select(a => a.Label))}");
        }

        var pending = bp.Tasks.Where(t => !t.Placed).ToList();
        if (pending.Count > 0)
            sb.AppendLine("Tác vụ mới lộ ra, sẽ được xếp chỗ ở lượt thiết kế kế tiếp: " + string.Join("; ", pending.Select(t => t.Name)));

        return sb.ToString().TrimEnd();
    }

    /// <summary>Mục lục màn hình (tên + là gì) — cho khung chat ở chế độ yêu cầu thay đổi.</summary>
    public static string? RenderScreenIndex(string? json)
    {
        var bp = UxBlueprintBuilder.Parse(json);
        if (bp.Screens.Count == 0 && bp.Tasks.All(t => t.Placed))
            return null;

        var sb = new StringBuilder();
        foreach (var screen in bp.Screens)
            sb.AppendLine($"* {screen.Name}" + (screen.Purpose.Length > 0 ? $" — {screen.Purpose}" : string.Empty));
        foreach (var task in bp.Tasks.Where(t => !t.Placed))
            sb.AppendLine($"* (tác vụ mới lộ ra, đang chờ xếp chỗ) {task.Name}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Khối cho lượt soạn Product Brief: mỗi màn hình bằng lời dễ hiểu (ai dùng, làm được gì), kèm tên màn giữ
    /// nguyên — để mục "Các màn hình chính" của Brief là ĐÚNG các màn của bản thiết kế, không phải một danh sách
    /// thứ hai model tự đếm lại từ hội thoại (và lệch với spec ở lượt đối chiếu Brief ↔ Spec).
    /// </summary>
    public static string? RenderBriefBlock(string? json)
    {
        var bp = UxBlueprintBuilder.Parse(json);
        if (bp.Screens.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var screen in bp.Screens)
        {
            var who = screen.Audience.Count > 0 ? $" (dành cho: {string.Join(", ", screen.Audience)})" : string.Empty;
            sb.AppendLine($"- {screen.Name}{who}: {screen.Purpose}");
            foreach (var action in screen.Actions)
                sb.AppendLine($"  - {action.Label}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Khối cho lượt sinh AI Design Spec: đủ chi tiết để spec dựng đúng từng màn (dạng màn, ai vào, từng hành
    /// động đặt ở đâu, nổi bật tới đâu, hiện theo trạng thái nào, mở form kiểu gì), điều hướng theo vai và hành
    /// trình của từng luồng.
    /// </summary>
    public static string? RenderSpecBlock(string? json)
    {
        var bp = UxBlueprintBuilder.Parse(json);
        if (bp.Screens.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("### Màn hình (theo đúng thứ tự này — mỗi màn một heading `### 6.n. <Tên>` ở mục 6)");
        foreach (var screen in bp.Screens)
        {
            sb.AppendLine($"* {screen.Name} — dạng: {PatternLabel(screen.Pattern)}"
                + (screen.Object.Length > 0 ? $" · đối tượng: {screen.Object}" : string.Empty)
                + (screen.Audience.Count > 0 ? $" · vai: {string.Join(", ", screen.Audience)}" : string.Empty)
                + (screen.Entry == UxEntry.Drilldown ? " · KHÔNG có mục menu, mở từ màn danh sách của đối tượng" : string.Empty));
            if (screen.Purpose.Length > 0)
                sb.AppendLine($"  - là gì: {screen.Purpose}");
            if (screen.Tabs.Count > 0)
                sb.AppendLine($"  - tab: {string.Join(", ", screen.Tabs)}");
            foreach (var action in screen.Actions)
                sb.AppendLine($"  - {ActionLine(bp, action)}");
            if (screen.Rationale.Length > 0)
                sb.AppendLine($"  - lý do thiết kế: {screen.Rationale}");
        }

        sb.AppendLine();
        sb.AppendLine("### Điều hướng theo vai");
        sb.Append(RenderNavigationTree(bp));

        var journeys = bp.Journeys.Where(j => j.Steps.Count > 0).ToList();
        if (journeys.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Hành trình của từng luồng (mỗi bước: ai → bấm gì → ở màn nào)");
            foreach (var journey in journeys)
                sb.AppendLine($"* {journey.Flow}: " + string.Join(" → ", journey.Steps.Select(JourneyStep)));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Cây menu theo vai, dạng bullet lồng — dùng cho khối spec và cho mục §5 chèn tất định.</summary>
    public static string RenderNavigationTree(UxBlueprint bp)
    {
        var sb = new StringBuilder();
        foreach (var nav in bp.Navigation)
        {
            sb.AppendLine($"- Vai «{nav.Role}» — mở đầu ở: {(nav.Landing.Length > 0 ? nav.Landing : "(màn đầu tiên)")}");
            foreach (var item in nav.Items)
            {
                sb.AppendLine($"  - {item.Label}");
                foreach (var child in item.Children)
                    sb.AppendLine($"    - {child.Label}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Khối cho lượt soạn bảng phân quyền: mỗi tác vụ một dòng, kèm màn đặt nó — dòng của bảng phân quyền LÀ
    /// các tác vụ này, model chỉ điền ai được làm và phạm vi dữ liệu.
    /// </summary>
    public static string RenderTasksForPermission(UxBlueprint bp, IEnumerable<UxTask>? only = null)
    {
        var sb = new StringBuilder();
        foreach (var task in only ?? bp.Tasks)
        {
            var screen = UxBlueprintBuilder.HomeOf(bp, task.Id)?.Name ?? "(chưa có màn)";
            var actors = task.Actors.Count > 0 ? $" · người làm theo luồng: {string.Join(", ", task.Actors)}" : string.Empty;
            var steps = task.FlowSteps.Count > 0 ? $" · phục vụ bước: {string.Join("; ", task.FlowSteps)}" : string.Empty;
            sb.AppendLine($"- {task.Id} | {screen} | {task.Name} [{task.Kind}]{actors}{steps}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string ActionLine(UxBlueprint bp, UxAction action)
    {
        var parts = new List<string> { $"«{action.Label}» ({action.Task})", PlacementLabel(action.Placement) };
        if (action.Prominence == UxProminence.Primary)
            parts.Add("nút CHÍNH");
        else if (action.Prominence == UxProminence.Overflow)
            parts.Add("trong menu phụ …");
        if (action.WhenState.Length > 0)
            parts.Add($"chỉ hiện khi trạng thái {action.WhenState}");
        if (action.Form == UxFormMode.Modal)
            parts.Add("mở modal");
        else if (action.Form == UxFormMode.Page)
            parts.Add("mở trang nhập liệu riêng");
        return string.Join(" · ", parts);
    }

    private static string JourneyStep(UxJourneyStep step)
        => step.Outside
            ? $"{step.Actor}: {step.Step} [ngoài phần mềm]"
            : $"{step.Actor}: «{(step.Action.Length > 0 ? step.Action : step.Step)}» @ {(step.Screen.Length > 0 ? step.Screen : "?")}";

    public static string PatternLabel(string pattern) => pattern switch
    {
        UxPattern.Dashboard => "trang tổng quan",
        UxPattern.List => "danh sách",
        UxPattern.ListDetail => "danh sách + khung chi tiết (master-detail)",
        UxPattern.Detail => "trang chi tiết",
        UxPattern.Form => "trang nhập liệu",
        UxPattern.Wizard => "nhập liệu nhiều bước",
        UxPattern.Queue => "hàng chờ xử lý",
        UxPattern.Board => "bảng kanban theo trạng thái",
        UxPattern.Calendar => "lịch",
        UxPattern.MasterData => "danh mục gom tab",
        UxPattern.Report => "báo cáo riêng",
        _ => pattern
    };

    public static string PlacementLabel(string placement) => placement switch
    {
        UxPlacement.Toolbar => "nút trên thanh công cụ đầu màn",
        UxPlacement.Row => "nút trên từng dòng",
        UxPlacement.Bulk => "thao tác trên các dòng đã chọn",
        UxPlacement.DetailHeader => "nút ở đầu khung chi tiết",
        UxPlacement.Inline => "sửa tại chỗ",
        UxPlacement.Filter => "bộ lọc / tab lọc",
        UxPlacement.Section => "một khối nội dung",
        _ => placement
    };

    /// <summary>Gói mô tả một dòng cho bản xuất/log: chỉ số + số phát hiện còn lại.</summary>
    public static string? RenderMetricsLine(string? json)
    {
        var bp = UxBlueprintBuilder.Parse(json);
        return bp.Screens.Count == 0 ? null : UxJourneyAnalyzer.Summary(bp.Metrics);
    }

    /// <summary>Các phát hiện thiết kế còn lại (sau vòng phản biện), mỗi dòng một phát hiện.</summary>
    public static string? RenderFindings(string? json)
    {
        var bp = UxBlueprintBuilder.Parse(json);
        if (bp.Findings.Count == 0)
            return null;
        return string.Join("\n", bp.Findings.Select(f => $"- [{f.Severity}] {f.Code}: {f.Message}"));
    }
}
