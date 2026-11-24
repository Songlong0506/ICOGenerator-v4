using System.Text.Json;
using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;
using static ICOGenerator.Services.Requirements.RequirementText;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Dựng (và đọc ngược) khối đầu vào JSON của lượt thiết kế — xem <see cref="UxDesignInput"/> cho lý do chọn
/// JSON. Hai chiều nằm cùng một chỗ để production và Prompt Evals không bao giờ lệch định dạng.
/// </summary>
public static partial class UxDesignInputBuilder
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Mốc mở khối JSON trong prompt — Prompt Evals tìm đúng mốc này để đọc ngược.</summary>
    public const string Heading = "## Dữ liệu thiết kế (JSON)";

    public static UxDesignInput Build(
        UxBlueprint inventory,
        string? flowMapJson,
        string? reportMapJson,
        string? permissionMatrixJson,
        IReadOnlyList<string> roles,
        string mode,
        UxBlueprint? current)
    {
        var input = new UxDesignInput
        {
            Mode = mode,
            Roles = roles.ToList(),
            Objects = inventory.Objects,
            Tasks = inventory.Tasks.Select(t => new UxDesignInputTask
            {
                Id = t.Id,
                Name = t.Name,
                Object = t.Object,
                Kind = t.Kind,
                Actors = t.Actors,
                FromState = t.FromState,
                ToState = t.ToState,
                Placed = t.Placed
            }).ToList(),
            DesignNotes = inventory.DesignNotes.Select(n => n.Screen.Length > 0 ? $"[{n.Screen}] {n.Text}" : n.Text).ToList()
        };

        foreach (var flow in FlowMapBuilder.Parse(flowMapJson))
        {
            var steps = flow.Steps.Where(s => s.Included && !string.IsNullOrWhiteSpace(s.Action)).ToList();
            if (steps.Count == 0)
                continue;
            input.Flows.Add(new UxDesignInputFlow
            {
                Name = flow.Name.Trim(),
                Kind = FlowKind.Normalize(flow.Kind),
                Steps = steps.Select(s => new UxDesignInputStep
                {
                    Actor = s.Actor.Trim(),
                    Action = s.Action.Trim(),
                    Outcome = s.Outcome.Trim(),
                    Outside = s.OutsideSoftware,
                    Task = s.OutsideSoftware ? string.Empty : UxJourneyAnalyzer.TaskFor(inventory, s.Action, s.Actor)?.Id ?? string.Empty
                }).ToList()
            });
        }

        foreach (var row in ReportMapBuilder.Parse(reportMapJson).Where(r => r.Included && !string.IsNullOrWhiteSpace(r.Report)))
        {
            var task = inventory.Tasks.FirstOrDefault(t => t.Kind == UxTaskKind.Report
                && Normalize(t.Name) == Normalize(UxInventoryBuilder.ReportTaskName(row)));
            input.Reports.Add(new UxDesignInputReport
            {
                Task = task?.Id ?? string.Empty,
                Report = row.Report.Trim(),
                Placement = !ReportMapBuilder.OnDashboard(row) ? "own-screen" : row.FilteredList ? "counter" : "dashboard",
                Screen = ReportMapBuilder.HostScreen(row),
                Breakdown = (row.Breakdown ?? string.Empty).Trim()
            });
        }

        var matrix = PermissionMatrixBuilder.Parse(permissionMatrixJson);
        foreach (var row in matrix.Where(r => r.TaskId.Length > 0))
        {
            var granted = row.Grants.Where(g => PermissionScope.IsGranted(g.Scope))
                .Select(g => $"{g.Role} ({PermissionScope.Label(g.Scope)})").ToList();
            if (granted.Count > 0)
                input.DataScopes.Add($"{row.TaskId}: {string.Join(", ", granted)}");
        }

        if (current != null)
        {
            input.CurrentScreens = current.Screens.Select(s => new UxScreenDraft
            {
                Name = s.Name,
                Pattern = s.Pattern,
                Object = s.Object,
                Purpose = s.Purpose,
                Audience = s.Audience,
                Entry = s.Entry,
                Tabs = s.Tabs,
                Actions = s.Actions,
                Rationale = s.Rationale
            }).ToList();
            input.CurrentNavigation = current.Navigation;
        }

        return input;
    }

    /// <summary>Khối đầu vào hoàn chỉnh của prompt thiết kế: mốc + JSON trong hàng rào.</summary>
    public static string Render(UxDesignInput input)
        => $"{Heading}\n```json\n{JsonSerializer.Serialize(input, WriteOptions)}\n```";

    /// <summary>
    /// Đọc ngược khối JSON từ một đầu vào đã render (scenario của Prompt Evals). Null khi không tìm thấy hay
    /// JSON hỏng.
    /// </summary>
    public static UxDesignInput? TryParse(string? rendered)
    {
        if (string.IsNullOrWhiteSpace(rendered))
            return null;
        var at = rendered.IndexOf(Heading, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var m = JsonFenceRegex().Match(rendered, at);
        if (!m.Success)
            return null;
        try
        {
            return JsonSerializer.Deserialize<UxDesignInput>(m.Groups[1].Value, ReadOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Dựng lại danh mục + bảng luồng từ một đầu vào đã đọc ngược — đủ để <see cref="UxBlueprintBuilder"/>,
    /// <see cref="UxBlueprintLint"/> và <see cref="UxJourneyAnalyzer"/> chạy y như ở production.
    /// </summary>
    public static (UxBlueprint Inventory, string FlowMapJson) ToInventory(UxDesignInput input)
    {
        var tasks = input.Tasks.Select(t => new UxTask
        {
            Id = t.Id,
            Name = t.Name,
            Object = t.Object,
            Kind = t.Kind,
            Actors = t.Actors ?? new(),
            FromState = t.FromState,
            ToState = t.ToState,
            Placed = t.Placed
        }).ToList();

        var flows = new List<FlowMapRow>();
        foreach (var flow in input.Flows)
        {
            flows.Add(new FlowMapRow
            {
                Name = flow.Name,
                Kind = flow.Kind,
                Steps = flow.Steps.Select(s => new FlowMapStep
                {
                    Actor = s.Actor,
                    Action = s.Action,
                    Outcome = s.Outcome,
                    OutsideSoftware = s.Outside
                }).ToList()
            });
            foreach (var step in flow.Steps.Where(s => s.Task.Length > 0))
            {
                var task = tasks.FirstOrDefault(t => UxBlueprintBuilder.SameId(t.Id, step.Task));
                if (task != null && !task.FlowSteps.Contains(step.Action))
                    task.FlowSteps.Add(step.Action);
            }
        }

        return (new UxBlueprint { Objects = input.Objects ?? new(), Tasks = tasks }, FlowMapBuilder.Serialize(flows));
    }

    [GeneratedRegex(@"```json\s*\n(.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRegex();
}
