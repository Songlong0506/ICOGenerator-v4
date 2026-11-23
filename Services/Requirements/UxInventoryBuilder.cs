using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;
using static ICOGenerator.Services.Requirements.RequirementText;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH của danh mục đối tượng + tác vụ (tầng 1–2 của <see cref="UxBlueprint"/>).
///
/// <para>
/// <b>ID ổn định là toàn bộ lý do tồn tại của lớp này.</b> Bảng phân quyền khoá theo ID tác vụ, nên một tác
/// vụ đã có phải giữ đúng ID của nó qua mọi lần thiết kế lại — kể cả khi model diễn đạt lại tên. Tác vụ mới
/// nhận ID kế tiếp; ID đã cấp không bao giờ được dùng lại cho việc khác (một quyền cũ sẽ dính sang nó).
/// </para>
///
/// <para>
/// <b>Ba luật, mỗi luật chặn một kiểu hỏng:</b>
/// </para>
/// <list type="bullet">
///   <item>Ô "phục vụ bước" nhận <b>chữ của bảng luồng</b>, không chữ model gõ lại — bản diễn đạt lại là báo động
///   giả chực chờ của phép đối chiếu bước luồng (<see cref="UncoveredSteps"/>).</item>
///   <item>Tác vụ báo cáo gieo <b>tất định</b> từ bảng báo cáo (<see cref="ReportTasks"/>), không để model kể lại:
///   bảng báo cáo đã chốt chỗ đứng của từng báo cáo, kể lại là mời nó gộp/tách/đổi tên.</item>
///   <item>Tác vụ cũ <b>chỉ được bỏ</b> khi nguồn của nó biến mất (mọi bước luồng nó phụ trách đã bị bỏ khỏi
///   bảng luồng, hoặc báo cáo của nó bị bỏ) — "model quên nêu" không phải "đã bỏ".</item>
/// </list>
/// </summary>
public static partial class UxInventoryBuilder
{
    public const int MaxObjects = 20;
    public const int MaxTasks = 60;
    public const int MaxStates = 10;
    public const int MaxActors = 6;
    public const int MaxStepsPerTask = 6;

    private const int MaxNameChars = 160;
    private const int MaxObjectNameChars = 60;
    private const int MaxDescriptionChars = 240;

    /// <summary>Một bước luồng đã chốt, cùng luồng chứa nó — đơn vị đối chiếu tác vụ ↔ luồng.</summary>
    public sealed record FlowStepRef(string Flow, string Kind, string Actor, string Action, string Outcome);

    /// <summary>Các bước luồng còn trong ứng dụng (đã tích, không làm ngoài phần mềm), theo đúng thứ tự luồng.</summary>
    public static List<FlowStepRef> FlowSteps(string? flowMapJson)
        => FlowMapBuilder.Parse(flowMapJson)
            .SelectMany(r => r.Steps
                .Where(s => s.Included && !s.OutsideSoftware && !string.IsNullOrWhiteSpace(s.Action))
                .Select(s => new FlowStepRef(r.Name.Trim(), FlowKind.Normalize(r.Kind), s.Actor.Trim(), s.Action.Trim(), s.Outcome.Trim())))
            .ToList();

    /// <summary>
    /// Dựng danh mục từ bản đang lưu (<paramref name="existing"/>) + đề xuất của model. Trả về bản ĐÃ GỘP:
    /// mọi đối tượng/tác vụ cũ còn nguồn đều còn, tác vụ mới mang ID mới và <c>Placed = false</c>.
    /// </summary>
    public static (List<UxObject> Objects, List<UxTask> Tasks) Build(
        UxBlueprint? existing,
        UxInventoryDraft? draft,
        string? flowMapJson,
        string? reportMapJson)
    {
        var objects = MergeObjects(existing?.Objects, draft?.Objects);
        var actions = FlowSteps(flowMapJson).Select(s => s.Action).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Tác vụ CŨ: giữ nguyên, trừ khi nguồn của nó đã biến mất. Tác vụ báo cáo luôn dựng lại từ bảng báo
        // cáo ở bước sau (giữ ID theo tên), nên ở đây bỏ hết để khỏi giữ một báo cáo đã bị loại.
        var tasks = new List<UxTask>();
        foreach (var task in existing?.Tasks ?? new List<UxTask>())
        {
            if (task.Kind == UxTaskKind.Report)
                continue;

            var clean = CloneTask(task);
            if (clean.FlowSteps.Count > 0)
            {
                clean.FlowSteps = clean.FlowSteps.Select(s => MatchStep(s, actions)).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // Mọi bước nó phụ trách đã bị bỏ khỏi bảng luồng ⇒ tác vụ của luồng không còn nguồn.
                if (clean.FlowSteps.Count == 0 && clean.Source == UxTaskSource.Flow)
                    continue;
            }
            tasks.Add(clean);
        }

        var nextId = NextNumber(existing?.Tasks.Select(t => t.Id), existing?.LastTaskNumber ?? 0);
        foreach (var proposed in draft?.Tasks ?? new List<UxTaskDraft>())
        {
            var task = CleanDraft(proposed, objects, actions);
            if (task == null || task.Kind == UxTaskKind.Report)
                continue;

            var match = FindSame(tasks, task);
            if (match != null)
            {
                Absorb(match, task);
                continue;
            }

            if (tasks.Count >= MaxTasks)
                continue;

            task.Id = FormatId("T", nextId++);
            tasks.Add(task);
        }

        // Tác vụ báo cáo: TẤT ĐỊNH, giữ ID theo tên báo cáo.
        var oldReports = (existing?.Tasks ?? new List<UxTask>()).Where(t => t.Kind == UxTaskKind.Report).ToList();
        foreach (var report in ReportTasks(reportMapJson))
        {
            var old = oldReports.FirstOrDefault(t => Normalize(t.Name) == Normalize(report.Name));
            report.Id = old?.Id ?? FormatId("T", nextId++);
            report.Placed = old?.Placed ?? false;
            if (tasks.Count < MaxTasks)
                tasks.Add(report);
        }

        // Đối tượng mà một tác vụ nhắc tên mà model quên kể ⇒ vẫn phải có mặt: thiếu nó thì không có "nhà"
        // nào cho các tác vụ đó.
        foreach (var name in tasks.Select(t => t.Object).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (objects.Count < MaxObjects && !objects.Any(o => Normalize(o.Name) == Normalize(name)))
                objects.Add(new UxObject { Name = name });
        }

        return (objects, tasks);
    }

    /// <summary>
    /// Tác vụ báo cáo gieo từ bảng báo cáo: mỗi báo cáo còn giữ một tác vụ, tên "<báo cáo> — <câu hỏi>".
    /// </summary>
    public static List<UxTask> ReportTasks(string? reportMapJson)
        => ReportMapBuilder.Parse(reportMapJson)
            .Where(r => r.Included && !string.IsNullOrWhiteSpace(r.Report))
            .Select(r => new UxTask
            {
                Name = Clip(ReportTaskName(r), MaxNameChars),
                Kind = UxTaskKind.Report,
                Source = UxTaskSource.Report
            })
            .GroupBy(t => Normalize(t.Name))
            .Select(g => g.First())
            .ToList();

    /// <summary>Tên tác vụ của một dòng báo cáo — cùng khuôn ở mọi tầng đọc nó.</summary>
    public static string ReportTaskName(ReportMapRow row)
    {
        var question = (row.Question ?? string.Empty).Trim();
        return question.Length > 0 ? $"{row.Report.Trim()} — {question}" : row.Report.Trim();
    }

    /// <summary>
    /// Các bước luồng đã chốt CHƯA tác vụ nào phụ trách. So khớp CHỨA-NHAU sau chuẩn hoá — người dùng sửa
    /// chữ của bước, model chép gần đúng; so nguyên văn thì báo động giả ở gần như mọi dòng.
    /// </summary>
    public static List<string> UncoveredSteps(IReadOnlyList<UxTask> tasks, string? flowMapJson)
    {
        var covered = tasks.SelectMany(t => t.FlowSteps).Select(Normalize).Where(s => s.Length > 0).ToList();
        return FlowSteps(flowMapJson)
            .Select(s => s.Action)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(action =>
            {
                var key = Normalize(action);
                return key.Length > 0 && !covered.Any(c => c.Contains(key, StringComparison.Ordinal) || key.Contains(c, StringComparison.Ordinal));
            })
            .ToList();
    }

    /// <summary>
    /// Nhận các bước model khai là làm ngoài phần mềm — chỉ những bước ĐANG chưa có tác vụ nào phụ trách, kéo về
    /// đúng chữ bảng luồng. Bước đã có tác vụ thắng lời khai "ngoài phần mềm" về chính nó.
    /// </summary>
    public static List<UxOutsideStep> ResolveOutside(IEnumerable<UxOutsideStep>? proposed, IReadOnlyList<string> uncovered)
    {
        var result = new List<UxOutsideStep>();
        foreach (var item in proposed ?? Enumerable.Empty<UxOutsideStep>())
        {
            var step = MatchStep(item?.Step, uncovered);
            if (step == null || result.Any(r => Normalize(r.Step) == Normalize(step)))
                continue;
            result.Add(new UxOutsideStep { Step = step, Reason = Clip((item!.Reason ?? string.Empty).Trim(), MaxDescriptionChars) });
        }
        return result;
    }

    /// <summary>
    /// Lưới an toàn cuối: bước luồng vẫn không tác vụ nào nhận sau mọi lượt hỏi ⇒ dựng một tác vụ đúng bằng
    /// bước đó. Một bước người dùng đã chốt mà ứng dụng không có chỗ làm là lỗi đắt nhất của cả dây chuyền.
    /// </summary>
    public static void AddFallbackTasks(List<UxTask> tasks, IReadOnlyList<string> uncovered, string? flowMapJson, int lastNumber = 0)
    {
        var steps = FlowSteps(flowMapJson);
        var nextId = NextNumber(tasks.Select(t => t.Id), lastNumber);
        foreach (var action in uncovered)
        {
            if (tasks.Count >= MaxTasks)
                break;
            var step = steps.FirstOrDefault(s => Normalize(s.Action) == Normalize(action));
            tasks.Add(new UxTask
            {
                Id = FormatId("T", nextId++),
                Name = Clip(action, MaxNameChars),
                Kind = string.IsNullOrWhiteSpace(step?.Outcome) ? UxTaskKind.Edit : UxTaskKind.Transition,
                Actors = string.IsNullOrWhiteSpace(step?.Actor) ? new List<string>() : new List<string> { step!.Actor },
                FlowSteps = new List<string> { action },
                Source = UxTaskSource.Flow
            });
        }
    }

    // ==== chuẩn hoá ====

    private static List<UxObject> MergeObjects(IEnumerable<UxObject>? existing, IEnumerable<UxObject>? proposed)
    {
        var result = new List<UxObject>();
        foreach (var source in (existing ?? Enumerable.Empty<UxObject>()).Concat(proposed ?? Enumerable.Empty<UxObject>()))
        {
            var name = CleanName(source?.Name, MaxObjectNameChars);
            if (name.Length == 0)
                continue;

            var states = CleanList(source!.States, MaxStates, MaxObjectNameChars);
            var found = result.FirstOrDefault(o => Normalize(o.Name) == Normalize(name));
            if (found == null)
            {
                if (result.Count >= MaxObjects)
                    continue;
                result.Add(new UxObject
                {
                    Name = name,
                    Description = Clip((source.Description ?? string.Empty).Trim(), MaxDescriptionChars),
                    IsCatalog = source.IsCatalog,
                    States = states,
                    FieldCount = Math.Clamp(source.FieldCount, 0, 60),
                    HasLineItems = source.HasLineItems
                });
                continue;
            }

            // Đề xuất mới bồi thêm vào đối tượng đã có, không viết đè bằng ô rỗng.
            if (found.Description.Length == 0)
                found.Description = Clip((source.Description ?? string.Empty).Trim(), MaxDescriptionChars);
            foreach (var state in states.Where(s => !found.States.Any(x => Normalize(x) == Normalize(s))))
            {
                if (found.States.Count < MaxStates)
                    found.States.Add(state);
            }
            found.IsCatalog |= source.IsCatalog;
            found.HasLineItems |= source.HasLineItems;
            if (source.FieldCount > 0)
                found.FieldCount = Math.Clamp(source.FieldCount, 0, 60);
        }
        return result;
    }

    private static UxTask? CleanDraft(UxTaskDraft? proposed, IReadOnlyList<UxObject> objects, IReadOnlyList<string> actions)
    {
        var name = Clip(OneLine(proposed?.Name), MaxNameChars).Trim();
        if (name.Length == 0)
            return null;

        var objectName = CleanName(proposed!.Object, MaxObjectNameChars);
        var known = objects.FirstOrDefault(o => Normalize(o.Name) == Normalize(objectName));
        if (known != null)
            objectName = known.Name; // chữ của danh mục đối tượng, không chữ model gõ lại

        var toState = CleanName(proposed.ToState, MaxObjectNameChars);
        return new UxTask
        {
            Name = name,
            Object = objectName,
            Kind = NormalizeKind(proposed.Kind, toState),
            Actors = CleanList(proposed.Actors, MaxActors, MaxObjectNameChars),
            FlowSteps = (proposed.FlowSteps ?? new List<string>())
                .Select(s => MatchStep(s, actions)).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxStepsPerTask).ToList(),
            FromState = CleanName(proposed.FromState, MaxObjectNameChars),
            ToState = toState,
            Source = UxTaskSource.All.Contains((proposed.Source ?? string.Empty).Trim().ToLowerInvariant())
                ? proposed.Source!.Trim().ToLowerInvariant()
                : UxTaskSource.Conversation
        };
    }

    // Cùng một tác vụ: trùng tên sau chuẩn hoá, HOẶC cùng đối tượng + cùng loại + cùng trạng thái đích + cùng
    // bước luồng (model diễn đạt lại tên của đúng một việc).
    private static UxTask? FindSame(IEnumerable<UxTask> tasks, UxTask candidate)
    {
        var name = Normalize(candidate.Name);
        foreach (var task in tasks)
        {
            if (Normalize(task.Name) == name)
                return task;

            if (candidate.FlowSteps.Count > 0
                && Normalize(task.Object) == Normalize(candidate.Object)
                && task.Kind == candidate.Kind
                && Normalize(task.ToState) == Normalize(candidate.ToState)
                && task.FlowSteps.Count == candidate.FlowSteps.Count
                && task.FlowSteps.All(s => candidate.FlowSteps.Any(c => Normalize(c) == Normalize(s))))
                return task;
        }
        return null;
    }

    private static void Absorb(UxTask target, UxTask source)
    {
        foreach (var actor in source.Actors.Where(a => !target.Actors.Any(x => Normalize(x) == Normalize(a))))
        {
            if (target.Actors.Count < MaxActors)
                target.Actors.Add(actor);
        }
        foreach (var step in source.FlowSteps.Where(s => !target.FlowSteps.Any(x => Normalize(x) == Normalize(s))))
        {
            if (target.FlowSteps.Count < MaxStepsPerTask)
                target.FlowSteps.Add(step);
        }
        if (target.Object.Length == 0)
            target.Object = source.Object;
        if (target.FromState.Length == 0)
            target.FromState = source.FromState;
        if (target.ToState.Length == 0)
            target.ToState = source.ToState;
    }

    private static UxTask CloneTask(UxTask task) => new()
    {
        Id = task.Id,
        Name = Clip(OneLine(task.Name), MaxNameChars),
        Object = task.Object ?? string.Empty,
        Kind = NormalizeKind(task.Kind, task.ToState),
        Actors = CleanList(task.Actors, MaxActors, MaxObjectNameChars),
        FlowSteps = (task.FlowSteps ?? new List<string>()).ToList(),
        FromState = task.FromState ?? string.Empty,
        ToState = task.ToState ?? string.Empty,
        Source = task.Source ?? UxTaskSource.Conversation,
        Placed = task.Placed
    };

    /// <summary>Loại tác vụ hợp lệ; không nhận ra mà có trạng thái đích ⇒ chuyển trạng thái, không thì xem danh sách.</summary>
    public static string NormalizeKind(string? raw, string? toState)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (UxTaskKind.All.Contains(value))
            return value;
        return string.IsNullOrWhiteSpace(toState) ? UxTaskKind.Browse : UxTaskKind.Transition;
    }

    /// <summary>
    /// Kéo một chuỗi model gõ về đúng một bước của <paramref name="actions"/>: khớp chính xác trước, rồi
    /// chứa-nhau (một chiều bất kỳ) khi chỉ MỘT bước khớp — nhiều bước cùng khớp là mơ hồ, bỏ.
    /// </summary>
    public static string? MatchStep(string? proposed, IReadOnlyList<string> actions)
    {
        var key = Normalize(proposed);
        if (key.Length == 0)
            return null;

        var exact = actions.FirstOrDefault(a => Normalize(a) == key);
        if (exact != null)
            return exact;

        var contains = actions.Where(a =>
        {
            var n = Normalize(a);
            return n.Length > 0 && (n.Contains(key, StringComparison.Ordinal) || key.Contains(n, StringComparison.Ordinal));
        }).ToList();
        return contains.Count == 1 ? contains[0] : null;
    }

    private static string CleanName(string? raw, int max)
        => Clip(string.Join(' ', OneLine(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries)), max).Trim();

    private static List<string> CleanList(IEnumerable<string>? values, int cap, int max)
    {
        var result = new List<string>();
        foreach (var raw in values ?? Enumerable.Empty<string>())
        {
            var value = CleanName(raw, max);
            if (value.Length == 0 || result.Any(x => Normalize(x) == Normalize(value)))
                continue;
            result.Add(value);
            if (result.Count >= cap)
                break;
        }
        return result;
    }

    /// <summary>
    /// Số kế tiếp cho ID dạng "T-07"/"S-03": lớn hơn mọi ID đang có VÀ lớn hơn <paramref name="lastNumber"/>
    /// (số lớn nhất từng cấp — xem <see cref="UxBlueprint.LastTaskNumber"/>), nên ID đã bỏ không bị dùng lại.
    /// </summary>
    public static int NextNumber(IEnumerable<string?>? ids, int lastNumber = 0)
    {
        var max = Math.Max(0, lastNumber);
        foreach (var id in ids ?? Enumerable.Empty<string?>())
        {
            var m = IdNumberRegex().Match(id ?? string.Empty);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
                max = Math.Max(max, n);
        }
        return max + 1;
    }

    public static string FormatId(string prefix, int number) => $"{prefix}-{number:00}";

    [GeneratedRegex(@"^[A-Za-z]+-(\d+)$")]
    private static partial Regex IdNumberRegex();
}
