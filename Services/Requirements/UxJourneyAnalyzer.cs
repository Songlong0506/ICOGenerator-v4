using ICOGenerator.Contracts.Requirements;
using static ICOGenerator.Services.Requirements.RequirementText;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Tính TẤT ĐỊNH hành trình của từng luồng nghiệp vụ qua bản thiết kế, và bộ chỉ số dùng để so hai bản thiết kế
/// với nhau (golden set, Prompt Evals, trước/sau mỗi lần đổi prompt hay model).
///
/// <para>
/// <b>Vì sao cần đo bằng code.</b> "Thiết kế này tốt hơn" là câu không ai kiểm được nếu chỉ có lời một model
/// chấm. Ba con số dưới đây đo đúng thứ người dùng cảm nhận khi bấm thử bản demo: phải mở bao nhiêu màn để làm
/// xong một việc (<see cref="UxJourney.ScreenSwitches"/>), một loại bản ghi bị rải ra bao nhiêu chỗ
/// (<see cref="UxMetrics.ScreensPerObject"/>), và menu dài tới đâu (<see cref="UxMetrics.MaxTopLevelNavItems"/>).
/// </para>
/// </summary>
public static class UxJourneyAnalyzer
{
    /// <summary>Hành trình của mọi luồng còn bước trong bảng luồng.</summary>
    public static List<UxJourney> Journeys(UxBlueprint blueprint, string? flowMapJson)
    {
        var result = new List<UxJourney>();
        foreach (var flow in FlowMapBuilder.Parse(flowMapJson))
        {
            var steps = flow.Steps.Where(s => s.Included && !string.IsNullOrWhiteSpace(s.Action)).ToList();
            if (steps.Count == 0)
                continue;

            var journey = new UxJourney { Flow = flow.Name.Trim(), Kind = FlowKind.Normalize(flow.Kind) };
            foreach (var step in steps)
            {
                if (step.OutsideSoftware)
                {
                    journey.Steps.Add(new UxJourneyStep { Step = step.Action.Trim(), Actor = step.Actor.Trim(), Outside = true });
                    continue;
                }

                var task = TaskFor(blueprint, step.Action, step.Actor);
                var (screen, action) = task == null ? (null, null) : ScreenFor(blueprint, task, step.Actor);
                journey.Steps.Add(new UxJourneyStep
                {
                    Step = step.Action.Trim(),
                    Actor = step.Actor.Trim(),
                    Task = task?.Id ?? string.Empty,
                    Screen = screen?.Name ?? string.Empty,
                    Action = action?.Label ?? string.Empty
                });
            }

            Count(journey, blueprint);
            result.Add(journey);
        }
        return result;
    }

    /// <summary>
    /// Đếm đổi màn và bàn giao. Đổi màn chỉ tính giữa hai bước liên tiếp CỦA CÙNG MỘT người làm (bàn giao sang
    /// vai khác thì dĩ nhiên vai kia mở màn của họ); mở trang chi tiết từ danh sách của cùng đối tượng là đi
    /// SÂU một tầng, không phải đổi chỗ làm việc — không tính.
    /// </summary>
    private static void Count(UxJourney journey, UxBlueprint blueprint)
    {
        UxJourneyStep? previous = null;
        foreach (var step in journey.Steps.Where(s => !s.Outside))
        {
            if (previous != null)
            {
                if (Normalize(previous.Actor) != Normalize(step.Actor))
                    journey.Handoffs++;
                else if (previous.Screen.Length > 0 && step.Screen.Length > 0
                         && previous.Screen != step.Screen
                         && !IsDrilldownPair(blueprint, previous.Screen, step.Screen))
                    journey.ScreenSwitches++;
            }
            previous = step;
        }
    }

    /// <summary>Hai màn là "danh sách ↔ chi tiết" của cùng một đối tượng (một màn mở từ màn kia).</summary>
    public static bool IsDrilldownPair(UxBlueprint blueprint, string a, string b)
    {
        var sa = blueprint.Screens.FirstOrDefault(s => s.Name == a);
        var sb = blueprint.Screens.FirstOrDefault(s => s.Name == b);
        if (sa == null || sb == null || sa.Object.Length == 0 || Normalize(sa.Object) != Normalize(sb.Object))
            return false;
        // Chỉ màn KHÔNG có mục menu mới là "đi sâu" từ màn kia. Một form có mục menu riêng là một chỗ làm việc
        // thứ hai cho cùng bản ghi — đúng thứ phép đếm đổi màn phải bắt.
        return sa.Entry == UxEntry.Drilldown || sb.Entry == UxEntry.Drilldown;
    }

    /// <summary>Tác vụ phụ trách bước luồng: ưu tiên tác vụ mà người làm bước đó là một actor của nó.</summary>
    public static UxTask? TaskFor(UxBlueprint blueprint, string action, string actor)
    {
        var key = Normalize(action);
        var candidates = blueprint.Tasks.Where(t => t.FlowSteps.Any(s =>
        {
            var n = Normalize(s);
            return n.Length > 0 && (n.Contains(key, StringComparison.Ordinal) || key.Contains(n, StringComparison.Ordinal));
        })).ToList();
        return candidates.FirstOrDefault(t => t.Actors.Any(a => Normalize(a) == Normalize(actor))) ?? candidates.FirstOrDefault();
    }

    /// <summary>Màn (và hành động) nơi <paramref name="actor"/> làm tác vụ: ưu tiên màn mà vai đó vào được.</summary>
    public static (UxScreen? Screen, UxAction? Action) ScreenFor(UxBlueprint blueprint, UxTask task, string actor)
    {
        var hosts = blueprint.Screens
            .Select(s => (Screen: s, Action: s.Actions.FirstOrDefault(a => UxBlueprintBuilder.SameId(a.Task, task.Id))))
            .Where(x => x.Action != null)
            .ToList();
        var best = hosts.FirstOrDefault(x => x.Screen.Audience.Any(a => Normalize(a) == Normalize(actor)));
        if (best.Screen == null)
            best = hosts.FirstOrDefault();
        return (best.Screen, best.Action);
    }

    /// <summary>Bộ chỉ số của bản thiết kế (sau khi đã có hành trình và phát hiện của lint).</summary>
    public static UxMetrics Metrics(UxBlueprint blueprint, string? flowMapJson)
    {
        var businessObjects = blueprint.Objects.Where(o => !o.IsCatalog).Select(o => Normalize(o.Name)).ToHashSet();
        var businessScreens = blueprint.Screens.Count(s => s.Object.Length > 0 && businessObjects.Contains(Normalize(s.Object)));
        var objectsWithScreens = blueprint.Screens
            .Where(s => s.Object.Length > 0 && businessObjects.Contains(Normalize(s.Object)))
            .Select(s => Normalize(s.Object)).Distinct().Count();

        var journeys = blueprint.Journeys.Count > 0 ? blueprint.Journeys : Journeys(blueprint, flowMapJson);

        return new UxMetrics
        {
            Objects = blueprint.Objects.Count,
            Tasks = blueprint.Tasks.Count,
            Screens = blueprint.Screens.Count,
            NavScreens = blueprint.Screens.Count(s => s.Entry == UxEntry.Nav),
            ScreensPerObject = objectsWithScreens == 0 ? 0 : Math.Round((double)businessScreens / objectsWithScreens, 2),
            MaxTopLevelNavItems = blueprint.Navigation.Count == 0 ? 0 : blueprint.Navigation.Max(n => n.Items.Count),
            AvgScreenSwitchesPerFlow = journeys.Count == 0 ? 0 : Math.Round(journeys.Average(j => j.ScreenSwitches), 2),
            ThinScreens = blueprint.Findings.Count(f => f.Code == UxLintCode.ThinScreen),
            DuplicateHomes = blueprint.Findings.Count(f => f.Code == UxLintCode.DuplicateHome),
            UnplacedTasks = blueprint.Tasks.Count(t => UxBlueprintBuilder.HomeOf(blueprint, t.Id) == null),
            UncoveredFlowSteps = UxInventoryBuilder.UncoveredSteps(blueprint.Tasks, flowMapJson).Count,
            Errors = blueprint.Findings.Count(f => f.Severity == UxSeverity.Error),
            Warnings = blueprint.Findings.Count(f => f.Severity == UxSeverity.Warning)
        };
    }

    /// <summary>Một dòng tóm tắt chỉ số — cho log, bản xuất hội thoại và đầu vào của judge ở Prompt Evals.</summary>
    public static string Summary(UxMetrics m)
        => $"{m.Screens} màn hình ({m.NavScreens} trên menu) cho {m.Objects} đối tượng / {m.Tasks} tác vụ · "
           + $"{m.ScreensPerObject:0.##} màn/đối tượng nghiệp vụ · menu dài nhất {m.MaxTopLevelNavItems} mục · "
           + $"đổi màn TB {m.AvgScreenSwitchesPerFlow:0.##}/luồng · màn mỏng {m.ThinScreens} · nhà trùng {m.DuplicateHomes} · "
           + $"tác vụ chưa có chỗ {m.UnplacedTasks} · bước luồng chưa có tác vụ {m.UncoveredFlowSteps} · "
           + $"lỗi {m.Errors} / cảnh báo {m.Warnings}";
}
