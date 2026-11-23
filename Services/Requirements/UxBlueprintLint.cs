using ICOGenerator.Contracts.Requirements;
using static ICOGenerator.Services.Requirements.RequirementText;

namespace ICOGenerator.Services.Requirements;

/// <summary>Mã luật của <see cref="UxBlueprintLint"/> — ổn định để test, eval và vòng phản biện bám vào.</summary>
public static class UxLintCode
{
    public const string UnplacedTask = "unplaced-task";
    public const string UncoveredStep = "uncovered-step";
    public const string EmptyScreen = "empty-screen";
    public const string TransitionScreen = "transition-as-screen";
    public const string DuplicateHome = "duplicate-home";
    public const string FormShouldBeModal = "form-should-be-modal";
    public const string ThinScreen = "thin-screen";
    public const string FragmentedFlow = "fragmented-flow";
    public const string AudienceMismatch = "audience-mismatch";
    public const string NavTooWide = "nav-too-wide";
    public const string NoNavigation = "no-navigation";
    public const string Unreachable = "unreachable-screen";
    public const string CatalogSprawl = "catalog-sprawl";
    public const string PrimaryOverload = "primary-overload";
    public const string ReportMisplaced = "report-misplaced";
    public const string OverloadedScreen = "overloaded-screen";
}

/// <summary>
/// Các luật thiết kế TẤT ĐỊNH chạy trên một bản thiết kế — thứ biến "UI/UX tốt" từ một lời dặn trong prompt
/// thành một phép kiểm đếm được.
///
/// <para>
/// <b>Lỗi (error) dẫn vòng phản biện</b>: <c>UxDesignService</c> đưa danh sách lỗi lại cho model sửa (tối đa
/// <c>UxDesignService.MaxRevisions</c> vòng). <b>Cảnh báo (warning)</b> được ghi lại vào bản thiết kế và vào
/// chỉ số, không chặn gì — chúng là thứ một người thiết kế vẫn có thể có lý do để giữ (một màn chỉ có một
/// nút, một menu dài).
/// </para>
///
/// <para>
/// Mỗi luật chặn đúng một kiểu thiết kế hỏng hay gặp ở bảng màn hình cũ (soạn theo câu chữ người dùng kể):
/// một màn cho mỗi bước luồng; "My X" và "All X" là hai màn trong khi chỉ khác phạm vi dữ liệu; đổi trạng thái
/// thành một trang riêng thay vì một nút trên bản ghi; năm danh mục là năm màn CRUD giống hệt nhau.
/// </para>
/// </summary>
public static class UxBlueprintLint
{
    /// <summary>Số mục menu gốc tối đa trước khi cảnh báo (7±2).</summary>
    public const int MaxTopLevelNav = 7;

    /// <summary>Form từ chừng này trường trở xuống, không có danh sách con, vừa một modal.</summary>
    public const int ModalFieldLimit = 8;

    /// <summary>Danh mục từ chừng này trường trở xuống là danh mục ĐƠN GIẢN — gom tab được.</summary>
    public const int SimpleCatalogFieldLimit = 6;

    /// <summary>Số màn danh mục đơn giản đứng riêng từ đó trở lên thì phải gom thành Master Data.</summary>
    public const int CatalogSprawlThreshold = 3;

    public const int MaxActionsBeforeWarning = 10;

    public static List<UxFinding> Check(UxBlueprint blueprint, string? flowMapJson)
    {
        var findings = new List<UxFinding>();

        CheckCoverage(blueprint, flowMapJson, findings);
        CheckScreens(blueprint, findings);
        CheckHomes(blueprint, findings);
        CheckCatalogs(blueprint, findings);
        CheckReports(blueprint, findings);
        CheckNavigation(blueprint, findings);
        CheckJourneys(blueprint, flowMapJson, findings);

        return findings;
    }

    public static List<UxFinding> Errors(IEnumerable<UxFinding> findings)
        => findings.Where(f => f.Severity == UxSeverity.Error).ToList();

    // ---- truy vết ----

    private static void CheckCoverage(UxBlueprint bp, string? flowMapJson, List<UxFinding> findings)
    {
        foreach (var task in bp.Tasks.Where(t => UxBlueprintBuilder.HomeOf(bp, t.Id) == null))
            Error(findings, UxLintCode.UnplacedTask, task.Id,
                $"Tác vụ {task.Id} «{task.Name}» chưa nằm trên màn hình nào — đặt nó thành một hành động trên màn NHÀ của đối tượng «{Or(task.Object, "liên quan")}».");

        foreach (var step in UxInventoryBuilder.UncoveredSteps(bp.Tasks, flowMapJson))
            Error(findings, UxLintCode.UncoveredStep, step,
                $"Bước luồng «{step}» không có tác vụ nào phụ trách.");
    }

    // ---- từng màn hình ----

    private static void CheckScreens(UxBlueprint bp, List<UxFinding> findings)
    {
        foreach (var screen in bp.Screens)
        {
            var tasks = TasksOf(bp, screen);

            if (screen.Actions.Count == 0)
            {
                Error(findings, UxLintCode.EmptyScreen, screen.Name,
                    $"Màn «{screen.Name}» không có hành động/khối nội dung nào — bỏ màn này, hoặc đặt lên nó các tác vụ mà nó sinh ra để phục vụ.");
                continue;
            }

            // Đổi trạng thái là một NÚT trên bản ghi, không phải một nơi chốn.
            if (screen.Entry == UxEntry.Nav
                && screen.Pattern is UxPattern.List or UxPattern.Form or UxPattern.Wizard or UxPattern.Detail
                && tasks.Count > 0 && tasks.All(t => t.Kind == UxTaskKind.Transition)
                && HasOtherHome(bp, screen))
                Error(findings, UxLintCode.TransitionScreen, screen.Name,
                    $"Màn «{screen.Name}» chỉ để đổi trạng thái «{screen.Object}» — chuyển trạng thái là một nút trên dòng/đầu trang chi tiết của màn nhà «{HomeName(bp, screen)}» (hiện theo trạng thái), không phải một màn riêng.");

            // Form nhỏ không có danh sách con ⇒ modal trên màn nhà, không phải một trang riêng.
            var obj = ObjectOf(bp, screen.Object);
            if (screen.Pattern is UxPattern.Form or UxPattern.Wizard
                && obj is { FieldCount: > 0 and <= ModalFieldLimit, HasLineItems: false }
                && tasks.All(t => t.Kind is UxTaskKind.Create or UxTaskKind.Edit)
                && HasOtherHome(bp, screen))
                Error(findings, UxLintCode.FormShouldBeModal, screen.Name,
                    $"Form «{screen.Name}» chỉ có ~{obj.FieldCount} thông tin, không có danh sách con — dùng modal mở từ «{HomeName(bp, screen)}» thay vì một màn riêng.");

            // Màn mỏng: một màn trên menu chỉ để làm đúng một việc.
            if (screen.Entry == UxEntry.Nav && screen.Actions.Count == 1
                && screen.Pattern is not (UxPattern.Dashboard or UxPattern.Report or UxPattern.Detail))
                Warning(findings, UxLintCode.ThinScreen, screen.Name,
                    $"Màn «{screen.Name}» chỉ có một hành động — cân nhắc gộp vào màn của cùng đối tượng/cùng vai, hoặc biến thành modal.");

            if (screen.Actions.Count > MaxActionsBeforeWarning)
                Warning(findings, UxLintCode.OverloadedScreen, screen.Name,
                    $"Màn «{screen.Name}» có {screen.Actions.Count} hành động — quá tải; tách theo vai/tab hoặc đẩy việc ít dùng vào menu phụ.");

            foreach (var group in screen.Actions
                         .Where(a => a.Prominence == UxProminence.Primary)
                         .GroupBy(a => a.Placement)
                         .Where(g => g.Count() > 1))
                Error(findings, UxLintCode.PrimaryOverload, screen.Name,
                    $"Màn «{screen.Name}» có {group.Count()} nút CHÍNH ở cùng vùng «{group.Key}» ({string.Join(", ", group.Select(a => a.Label))}) — mỗi vùng tối đa MỘT nút chính, còn lại là secondary/overflow.");

            foreach (var action in screen.Actions)
            {
                var task = bp.Tasks.FirstOrDefault(t => UxBlueprintBuilder.SameId(t.Id, action.Task));
                var humans = task?.Actors.Where(a => !UxRoles.IsSystem(a)).ToList() ?? new List<string>();
                if (task == null || humans.Count == 0 || screen.Audience.Count == 0)
                    continue;
                if (!humans.Any(a => screen.Audience.Any(x => Normalize(x) == Normalize(a))))
                    Warning(findings, UxLintCode.AudienceMismatch, screen.Name,
                        $"Hành động «{action.Label}» ({task.Id}) do {string.Join(", ", task.Actors)} làm, nhưng màn «{screen.Name}» không mở cho vai đó.");
            }
        }
    }

    // ---- một đối tượng một "nhà" ----

    private static void CheckHomes(UxBlueprint bp, List<UxFinding> findings)
    {
        foreach (var group in bp.Screens
                     .Where(s => s.Object.Length > 0 && UxPattern.Home.Contains(s.Pattern) && s.Pattern != UxPattern.MasterData)
                     .GroupBy(s => Normalize(s.Object))
                     .Where(g => g.Count() > 1))
        {
            var names = group.Select(s => $"«{s.Name}»").ToList();
            Error(findings, UxLintCode.DuplicateHome, group.First().Object,
                $"Đối tượng «{group.First().Object}» có {names.Count} màn danh sách ({string.Join(", ", names)}). Mỗi đối tượng MỘT nhà: các vai khác nhau ở PHẠM VI DỮ LIỆU (của mình / đơn vị / tất cả) và ở NÚT được thấy thì dùng chung một màn (lọc theo vai, tab trạng thái). Chỉ tách khi nội dung thật sự khác — khi đó đổi một màn thành hàng chờ (queue) hoặc báo cáo.");
        }
    }

    // ---- danh mục ----

    private static void CheckCatalogs(UxBlueprint bp, List<UxFinding> findings)
    {
        var simpleCatalogScreens = bp.Screens
            .Where(s => s.Pattern != UxPattern.MasterData)
            .Where(s => ObjectOf(bp, s.Object) is { IsCatalog: true, HasLineItems: false } o && o.FieldCount <= SimpleCatalogFieldLimit)
            .ToList();
        if (simpleCatalogScreens.Count >= CatalogSprawlThreshold)
            Error(findings, UxLintCode.CatalogSprawl, string.Join(", ", simpleCatalogScreens.Select(s => s.Name)),
                $"{simpleCatalogScreens.Count} danh mục đơn giản đang là {simpleCatalogScreens.Count} màn CRUD giống hệt nhau ({string.Join(", ", simpleCatalogScreens.Select(s => $"«{s.Name}»"))}) — gom thành MỘT màn master-data, mỗi danh mục một tab.");
    }

    // ---- báo cáo ----

    private static void CheckReports(UxBlueprint bp, List<UxFinding> findings)
    {
        foreach (var task in bp.Tasks.Where(t => t.Kind == UxTaskKind.Report))
        {
            var hosts = bp.Screens.Where(s => s.Actions.Any(a => UxBlueprintBuilder.SameId(a.Task, task.Id))).ToList();
            if (hosts.Count > 0 && hosts.All(s => s.Pattern is not (UxPattern.Dashboard or UxPattern.Report)))
                Error(findings, UxLintCode.ReportMisplaced, task.Id,
                    $"Báo cáo {task.Id} «{task.Name}» đang nằm trên «{hosts[0].Name}» — báo cáo thuộc màn tổng quan (dashboard) hoặc màn báo cáo riêng, đúng chỗ bảng báo cáo đã ghi.");
        }
    }

    // ---- điều hướng ----

    private static void CheckNavigation(UxBlueprint bp, List<UxFinding> findings)
    {
        var actors = bp.Tasks.SelectMany(t => t.Actors).Where(a => !UxRoles.IsSystem(a))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var role in actors)
        {
            var nav = bp.Navigation.FirstOrDefault(n => Normalize(n.Role) == Normalize(role));
            if (nav == null || nav.Items.Count == 0)
                Error(findings, UxLintCode.NoNavigation, role, $"Vai «{role}» làm tác vụ nhưng không có menu nào.");
        }

        foreach (var nav in bp.Navigation.Where(n => n.Items.Count > MaxTopLevelNav))
            Warning(findings, UxLintCode.NavTooWide, nav.Role,
                $"Menu của «{nav.Role}» có {nav.Items.Count} mục gốc (> {MaxTopLevelNav}) — gom theo việc (nhóm xổ xuống) để luồng chính không bị đẩy xuống cuối.");

        var inNav = bp.Navigation.SelectMany(n => UxBlueprintBuilder.FlattenScreens(n.Items)).ToHashSet();
        foreach (var screen in bp.Screens)
        {
            if (screen.Entry == UxEntry.Nav && !inNav.Contains(screen.Name))
                Error(findings, UxLintCode.Unreachable, screen.Name, $"Màn «{screen.Name}» không có mục menu của vai nào.");
            else if (screen.Entry == UxEntry.Drilldown && !HasOtherHome(bp, screen))
                Error(findings, UxLintCode.Unreachable, screen.Name,
                    $"Màn «{screen.Name}» chỉ mở từ màn khác nhưng đối tượng «{Or(screen.Object, "?")}» không có màn danh sách nào để mở nó.");
        }
    }

    // ---- hành trình ----

    private static void CheckJourneys(UxBlueprint bp, string? flowMapJson, List<UxFinding> findings)
    {
        foreach (var journey in UxJourneyAnalyzer.Journeys(bp, flowMapJson))
        {
            var pairs = new List<string>();
            UxJourneyStep? previous = null;
            foreach (var step in journey.Steps.Where(s => !s.Outside))
            {
                if (previous != null
                    && Normalize(previous.Actor) == Normalize(step.Actor)
                    && previous.Screen.Length > 0 && step.Screen.Length > 0
                    && previous.Screen != step.Screen
                    && SameObject(bp, previous.Screen, step.Screen)
                    && !UxJourneyAnalyzer.IsDrilldownPair(bp, previous.Screen, step.Screen))
                    pairs.Add($"«{previous.Step}» ở {previous.Screen} → «{step.Step}» ở {step.Screen}");
                previous = step;
            }

            if (pairs.Count > 0)
                Error(findings, UxLintCode.FragmentedFlow, journey.Flow,
                    $"Luồng «{journey.Flow}»: cùng một người làm, cùng một loại bản ghi mà phải đổi màn — {string.Join("; ", pairs)}. Đặt các bước liên tiếp của cùng người trên cùng bản ghi ở CÙNG một màn (nút theo trạng thái).");
        }
    }

    // ==== tiện ích ====

    private static List<UxTask> TasksOf(UxBlueprint bp, UxScreen screen)
        => screen.Actions
            .Select(a => bp.Tasks.FirstOrDefault(t => UxBlueprintBuilder.SameId(t.Id, a.Task)))
            .OfType<UxTask>()
            .ToList();

    private static UxObject? ObjectOf(UxBlueprint bp, string name)
        => name.Length == 0 ? null : bp.Objects.FirstOrDefault(o => Normalize(o.Name) == Normalize(name));

    private static bool HasOtherHome(UxBlueprint bp, UxScreen screen)
        => screen.Object.Length > 0 && bp.Screens.Any(s => s != screen
            && Normalize(s.Object) == Normalize(screen.Object)
            && UxPattern.Home.Contains(s.Pattern));

    private static string HomeName(UxBlueprint bp, UxScreen screen)
        => bp.Screens.FirstOrDefault(s => s != screen && Normalize(s.Object) == Normalize(screen.Object) && UxPattern.Home.Contains(s.Pattern))?.Name
           ?? screen.Object;

    private static bool SameObject(UxBlueprint bp, string a, string b)
    {
        var oa = bp.Screens.FirstOrDefault(s => s.Name == a)?.Object ?? string.Empty;
        var ob = bp.Screens.FirstOrDefault(s => s.Name == b)?.Object ?? string.Empty;
        return oa.Length > 0 && Normalize(oa) == Normalize(ob);
    }

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static void Error(List<UxFinding> findings, string code, string subject, string message)
        => findings.Add(new UxFinding { Code = code, Severity = UxSeverity.Error, Subject = subject, Message = message });

    private static void Warning(List<UxFinding> findings, string code, string subject, string message)
        => findings.Add(new UxFinding { Code = code, Severity = UxSeverity.Warning, Subject = subject, Message = message });
}
