using System.Text;
using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chèn TẤT ĐỊNH bản thiết kế UX vào AI Design Spec vừa sinh: thay thân mục <c>## 5. Navigation Structure</c>
/// bằng cây menu của bản thiết kế, và đặt hai mục máy đọc được <c>## 6c. UX Blueprint</c> (từng màn: dạng màn,
/// vai, từng hành động kèm vị trí/độ nổi/điều kiện trạng thái) + <c>## 6d. UX Journeys</c> (hành trình của
/// từng luồng).
///
/// <para>
/// <b>Vì sao chèn bằng code chứ không dặn model chép.</b> Spec là đầu vào DUY NHẤT của bước dựng POC, và mọi
/// cổng phía sau (audit POC, kịch bản nghiệm thu, chấm hình UiUx) chỉ so POC với spec. Thứ model chép lại thì
/// trôi theo cách model diễn đạt; thứ code chèn thì đúng từng chữ với bản thiết kế — và
/// <c>PocSpec</c> đọc được nó để kiểm hành động có thật trên đúng màn (<c>PocAudit.CheckBlueprintActions</c>).
/// </para>
/// </summary>
public static partial class UxSpecSections
{
    public const string BlueprintHeading = "## 6c. UX Blueprint";
    public const string JourneysHeading = "## 6d. UX Journeys";

    /// <summary>Spec đã chèn bản thiết kế. Không có bản thiết kế ⇒ trả nguyên spec.</summary>
    public static string Apply(string? spec, string? blueprintJson)
    {
        var content = spec ?? string.Empty;
        var bp = UxBlueprintBuilder.Parse(blueprintJson);
        if (bp.Screens.Count == 0 || content.Trim().Length == 0)
            return content;

        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();

        RemoveSection(lines, l => l.StartsWith(BlueprintHeading, StringComparison.Ordinal));
        RemoveSection(lines, l => l.StartsWith(JourneysHeading, StringComparison.Ordinal));
        ReplaceNavigation(lines, bp);

        var appendix = (RenderBlueprintSection(bp) + "\n" + RenderJourneysSection(bp)).TrimEnd().Split('\n');
        var insertAt = IndexOfSection(lines, IsUiDirectionHeading);
        if (insertAt < 0)
            insertAt = lines.Count;
        lines.InsertRange(insertAt, appendix.Append(string.Empty));

        return string.Join("\n", lines).TrimEnd() + "\n";
    }

    /// <summary>Mục <c>## 6c</c>: một <c>### Tên màn</c> mỗi màn, rồi mỗi hành động một dòng khuôn cố định.</summary>
    public static string RenderBlueprintSection(UxBlueprint bp)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BlueprintHeading);
        sb.AppendLine("(Bản thiết kế UX đã chốt — hệ thống chèn tự động. POC dựng ĐÚNG như sau: mỗi hành động là một nút/khối mang ĐÚNG nhãn ghi ở cuối dòng, đặt đúng vị trí; nút có `when` chỉ hiện ở bản ghi đang ở trạng thái đó; `form: modal` là mở modal, `form: page` là sang trang nhập liệu.)");
        foreach (var screen in bp.Screens)
        {
            sb.AppendLine();
            sb.AppendLine($"### {screen.Name}");
            sb.AppendLine($"- Pattern: {screen.Pattern} · Object: {Or(screen.Object)} · Audience: {Or(string.Join(", ", screen.Audience))} · Entry: {screen.Entry}"
                + (screen.Tabs.Count > 0 ? $" · Tabs: {string.Join(", ", screen.Tabs)}" : string.Empty));
            foreach (var action in screen.Actions)
                sb.AppendLine($"- Action {action.Task} | {action.Placement} | {action.Prominence} | when: {Or(action.WhenState)} | form: {action.Form} | {action.Label}");
        }
        return sb.ToString();
    }

    public static string RenderJourneysSection(UxBlueprint bp)
    {
        var journeys = bp.Journeys.Where(j => j.Steps.Count > 0).ToList();
        if (journeys.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine(JourneysHeading);
        sb.AppendLine("(Hành trình của từng luồng qua các màn — kịch bản nghiệm thu đi đúng các đường này.)");
        foreach (var journey in journeys)
        {
            sb.AppendLine($"- {journey.Flow}:");
            foreach (var step in journey.Steps)
            {
                sb.AppendLine(step.Outside
                    ? $"  - {step.Actor}: {step.Step} — ngoài phần mềm"
                    : $"  - {step.Actor}: bấm «{(step.Action.Length > 0 ? step.Action : step.Step)}» ở {Or(step.Screen)}");
            }
        }
        return sb.ToString();
    }

    private static void ReplaceNavigation(List<string> lines, UxBlueprint bp)
    {
        var tree = UxBlueprintRenderer.RenderNavigationTree(bp).TrimEnd().Split('\n');
        var start = IndexOfSection(lines, IsNavigationHeading);
        if (start < 0)
        {
            var screens = IndexOfSection(lines, IsScreensHeading);
            var at = screens < 0 ? lines.Count : screens;
            lines.InsertRange(at, new[] { "## 5. Navigation Structure" }.Concat(tree).Append(string.Empty));
            return;
        }

        var end = NextLevel2(lines, start + 1);
        lines.RemoveRange(start + 1, end - start - 1);
        lines.InsertRange(start + 1, tree.Append(string.Empty));
    }

    private static void RemoveSection(List<string> lines, Func<string, bool> isHeading)
    {
        var start = lines.FindIndex(l => isHeading(l.Trim()));
        if (start < 0)
            return;
        var end = NextLevel2(lines, start + 1);
        lines.RemoveRange(start, end - start);
    }

    private static int IndexOfSection(List<string> lines, Func<string, bool> isHeading)
        => lines.FindIndex(l => Level2Regex().IsMatch(l) && isHeading(l.Trim().ToLowerInvariant()));

    private static int NextLevel2(List<string> lines, int from)
    {
        for (var i = from; i < lines.Count; i++)
        {
            if (Level2Regex().IsMatch(lines[i]))
                return i;
        }
        return lines.Count;
    }

    private static bool IsNavigationHeading(string heading) => heading.Contains("navigation") || heading.Contains("điều hướng");

    private static bool IsScreensHeading(string heading) => heading.Contains("screens to generate");

    private static bool IsUiDirectionHeading(string heading) => heading.Contains("ui/ux direction") || heading.StartsWith("## 7.");

    private static string Or(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    [GeneratedRegex(@"^##\s+\S")]
    private static partial Regex Level2Regex();
}
