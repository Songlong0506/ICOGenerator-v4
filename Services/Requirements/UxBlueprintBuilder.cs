using System.Text.Json;
using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Artifacts;
using static ICOGenerator.Services.Requirements.RequirementText;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH của tầng 3 (màn hình + điều hướng) của <see cref="UxBlueprint"/>: kéo đề xuất của model
/// về đúng danh mục tác vụ (<see cref="FromDraft"/>), rồi VÁ những gì vòng phản biện vẫn để sót
/// (<see cref="Repair"/>) — để bản thiết kế được lưu luôn đủ: mọi tác vụ có chỗ, mọi vai có trang đích, mọi
/// màn hình có đường vào.
///
/// <para>
/// Đây KHÔNG phải nơi ra quyết định thiết kế. Quyết định là của model UiUx (prompt <c>UiUx/ux-blueprint.v1.md</c>)
/// và của vòng phản biện do <see cref="UxBlueprintLint"/> dẫn dắt. Builder chỉ làm hai việc mà model không được
/// phép làm hỏng: (1) chỉ nhận thứ trỏ vào tác vụ/đối tượng/vai CÓ THẬT, (2) không bao giờ lưu một bản thiếu
/// chỗ cho một tác vụ người dùng đã đặt hàng.
/// </para>
/// </summary>
public static partial class UxBlueprintBuilder
{
    public const int MaxScreens = 40;
    public const int MaxActionsPerScreen = 16;
    public const int MaxTabs = 10;
    public const int MaxNavItems = 24;

    private const int MaxScreenNameChars = 60;
    private const int MaxLabelChars = 80;
    private const int MaxTextChars = 240;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    // ==== lưu / đọc ====

    public static UxBlueprint Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new UxBlueprint();
        try
        {
            var bp = JsonSerializer.Deserialize<UxBlueprint>(json, ReadOptions) ?? new UxBlueprint();
            bp.Objects ??= new();
            bp.Tasks ??= new();
            bp.Screens ??= new();
            bp.Navigation ??= new();
            bp.Journeys ??= new();
            bp.Findings ??= new();
            bp.DesignNotes ??= new();
            bp.Metrics ??= new();
            foreach (var screen in bp.Screens)
            {
                screen.Actions ??= new();
                screen.Audience ??= new();
                screen.Tabs ??= new();
            }
            foreach (var task in bp.Tasks)
            {
                task.Actors ??= new();
                task.FlowSteps ??= new();
            }
            return bp;
        }
        catch
        {
            return new UxBlueprint();
        }
    }

    public static string Serialize(UxBlueprint blueprint) => JsonSerializer.Serialize(blueprint);

    /// <summary>Đã có bản thiết kế (ít nhất một màn hình) chưa.</summary>
    public static bool IsDesigned(string? json) => Parse(json).Screens.Count > 0;

    /// <summary>Còn tác vụ CHƯA XẾP CHỖ (lượt danh mục vừa thêm) — một điều kiện của <see cref="UxDesignGate.NeedsRedesign"/>.</summary>
    public static bool HasPending(string? json) => Parse(json).Tasks.Any(t => !t.Placed);

    /// <summary>Tên các màn hình theo thứ tự thiết kế — danh sách màn của spec và của bản demo.</summary>
    public static List<string> ScreenNames(string? json) => Parse(json).Screens.Select(s => s.Name).ToList();

    /// <summary>Màn hình đặt tác vụ <paramref name="taskId"/> (màn đầu tiên chứa nó), null khi chưa có chỗ.</summary>
    public static UxScreen? HomeOf(UxBlueprint blueprint, string taskId)
        => blueprint.Screens.FirstOrDefault(s => s.Actions.Any(a => SameId(a.Task, taskId)));

    // ==== chuẩn hoá đề xuất của model ====

    /// <summary>
    /// Kéo bản đề xuất của model về đúng danh mục: hành động trỏ vào tác vụ không có thật bị bỏ; tên màn trùng
    /// gộp làm một; ID màn giữ theo tên của bản trước (<paramref name="previous"/>); vai lạ bị bỏ khỏi
    /// audience/điều hướng khi danh sách vai có thật đã biết.
    /// </summary>
    public static UxBlueprint FromDraft(
        UxBlueprintDraft? draft,
        UxBlueprint inventory,
        IReadOnlyList<string> roles,
        UxBlueprint? previous = null)
    {
        var result = new UxBlueprint
        {
            Version = inventory.Version,
            LastTaskNumber = inventory.LastTaskNumber,
            LastScreenNumber = Math.Max(inventory.LastScreenNumber, previous?.LastScreenNumber ?? 0),
            Objects = inventory.Objects,
            Tasks = inventory.Tasks,
            DesignNotes = inventory.DesignNotes
        };

        var nextScreen = UxInventoryBuilder.NextNumber(
            (previous?.Screens ?? new List<UxScreen>()).Select(s => s.Id), result.LastScreenNumber);

        foreach (var proposed in draft?.Screens ?? new List<UxScreenDraft>())
        {
            var name = CleanScreenName(proposed?.Name);
            if (name.Length == 0)
                continue;

            var screen = result.Screens.FirstOrDefault(s => Normalize(s.Name) == Normalize(name));
            if (screen == null)
            {
                if (result.Screens.Count >= MaxScreens)
                    continue;

                screen = new UxScreen
                {
                    Id = previous?.Screens.FirstOrDefault(s => Normalize(s.Name) == Normalize(name))?.Id
                         ?? UxInventoryBuilder.FormatId("S", nextScreen++),
                    Name = name,
                    Pattern = NormalizePattern(proposed!.Pattern),
                    Object = MatchObject(proposed.Object, inventory.Objects),
                    Purpose = Clip(OneLine(proposed.Purpose), MaxTextChars),
                    Entry = UxEntry.All.Contains((proposed.Entry ?? string.Empty).Trim().ToLowerInvariant())
                        ? proposed.Entry!.Trim().ToLowerInvariant()
                        : UxEntry.Nav,
                    Rationale = Clip(OneLine(proposed.Rationale), MaxTextChars)
                };
                result.Screens.Add(screen);
            }

            foreach (var role in MatchRoles(proposed!.Audience, roles))
            {
                if (!screen.Audience.Any(a => Normalize(a) == Normalize(role)))
                    screen.Audience.Add(role);
            }

            foreach (var tab in proposed.Tabs ?? new List<string>())
            {
                var value = Clip(OneLine(tab), MaxScreenNameChars);
                if (value.Length > 0 && screen.Tabs.Count < MaxTabs && !screen.Tabs.Any(t => Normalize(t) == Normalize(value)))
                    screen.Tabs.Add(value);
            }

            foreach (var action in proposed.Actions ?? new List<UxAction>())
            {
                var task = FindTask(action?.Task, inventory.Tasks);
                if (task == null || screen.Actions.Count >= MaxActionsPerScreen
                    || screen.Actions.Any(a => SameId(a.Task, task.Id)))
                    continue;
                screen.Actions.Add(NormalizeAction(action!, task, inventory.Objects));
            }
        }

        // ID màn hình: ghi nhận số lớn nhất vừa cấp để không bao giờ cấp lại.
        result.LastScreenNumber = Math.Max(result.LastScreenNumber, UxInventoryBuilder.NextNumber(result.Screens.Select(s => s.Id)) - 1);

        result.Navigation = NormalizeNavigation(draft?.Navigation, result.Screens, roles);
        return result;
    }

    /// <summary>
    /// Một hành động hợp lệ của tác vụ: nhãn mặc định là tên tác vụ, vị trí/độ nổi/cách mở form không nhận ra
    /// thì lấy mặc định theo loại tác vụ, điều kiện trạng thái kéo về đúng chữ vòng đời của đối tượng.
    /// </summary>
    private static UxAction NormalizeAction(UxAction proposed, UxTask task, IReadOnlyList<UxObject> objects)
    {
        var placement = (proposed.Placement ?? string.Empty).Trim().ToLowerInvariant();
        if (!UxPlacement.All.Contains(placement))
            placement = DefaultPlacement(task.Kind);

        var prominence = (proposed.Prominence ?? string.Empty).Trim().ToLowerInvariant();
        if (!UxProminence.All.Contains(prominence))
            prominence = UxProminence.Secondary;

        var form = (proposed.Form ?? string.Empty).Trim().ToLowerInvariant();
        if (!UxFormMode.All.Contains(form))
            form = task.Kind is UxTaskKind.Create or UxTaskKind.Edit ? UxFormMode.Modal : UxFormMode.None;

        var label = Clip(OneLine(proposed.Label), MaxLabelChars);
        var whenState = OneLine(proposed.WhenState);
        var states = objects.FirstOrDefault(o => Normalize(o.Name) == Normalize(task.Object))?.States ?? new List<string>();
        var knownState = states.FirstOrDefault(s => Normalize(s) == Normalize(whenState));
        // Nút chuyển trạng thái chỉ có nghĩa ở bản ghi đang ở trạng thái nguồn — model bỏ trống thì lấy đúng
        // trạng thái nguồn của tác vụ, để bản demo không hiện "Duyệt" trên một đơn đã duyệt.
        if (whenState.Length == 0 && task.Kind == UxTaskKind.Transition && task.FromState.Length > 0)
            whenState = task.FromState;

        return new UxAction
        {
            Task = task.Id,
            Label = label.Length > 0 ? label : Clip(task.Name, MaxLabelChars),
            Placement = placement,
            Prominence = prominence,
            WhenState = knownState ?? Clip(whenState, MaxScreenNameChars),
            Form = form
        };
    }

    /// <summary>Vị trí mặc định của một hành động theo loại tác vụ — dùng khi model bỏ trống và khi vá.</summary>
    public static string DefaultPlacement(string kind) => kind switch
    {
        UxTaskKind.Create or UxTaskKind.Import or UxTaskKind.Export or UxTaskKind.Configure => UxPlacement.Toolbar,
        UxTaskKind.Transition or UxTaskKind.Edit or UxTaskKind.Delete or UxTaskKind.View => UxPlacement.Row,
        UxTaskKind.Bulk => UxPlacement.Bulk,
        UxTaskKind.Report => UxPlacement.Section,
        _ => UxPlacement.Filter
    };

    private static List<UxNavigation> NormalizeNavigation(IEnumerable<UxNavigation>? proposed, IReadOnlyList<UxScreen> screens, IReadOnlyList<string> roles)
    {
        var result = new List<UxNavigation>();
        foreach (var nav in proposed ?? Enumerable.Empty<UxNavigation>())
        {
            var role = MatchRoles(new[] { nav?.Role ?? string.Empty }, roles).FirstOrDefault();
            if (role == null || result.Any(r => Normalize(r.Role) == Normalize(role)))
                continue;

            var items = NormalizeItems(nav!.Items, screens, depth: 0);
            var landing = MatchScreen(nav.Landing, screens);
            result.Add(new UxNavigation { Role = role, Landing = landing ?? string.Empty, Items = items });
        }
        return result;
    }

    private static List<UxNavItem> NormalizeItems(IEnumerable<UxNavItem>? items, IReadOnlyList<UxScreen> screens, int depth)
    {
        var result = new List<UxNavItem>();
        foreach (var item in items ?? Enumerable.Empty<UxNavItem>())
        {
            if (item == null || result.Count >= MaxNavItems)
                continue;

            var screen = MatchScreen(item.Screen, screens) ?? (item.Children is { Count: > 0 } ? null : MatchScreen(item.Label, screens));
            if (screen != null)
            {
                if (!ContainsScreen(result, screen))
                    result.Add(new UxNavItem { Label = screen, Screen = screen });
                continue;
            }

            // Nhóm: chỉ một tầng con (menu xổ xuống của bản demo không lồng sâu hơn).
            if (depth > 0 || item.Children is not { Count: > 0 })
                continue;
            var children = NormalizeItems(item.Children, screens, depth + 1)
                .Where(c => !ContainsScreen(result, c.Screen)).ToList();
            var label = Clip(OneLine(item.Label), MaxScreenNameChars);
            if (children.Count > 0 && label.Length > 0)
                result.Add(new UxNavItem { Label = label, Children = children });
        }
        return result;
    }

    // ==== vá tất định ====

    /// <summary>
    /// Vá những gì vòng phản biện còn để sót, để bản được LƯU luôn dùng được:
    /// tác vụ chưa có chỗ ⇒ đặt vào "nhà" của đối tượng (hoặc dựng nhà cho nó); vai của tác vụ không vào được màn
    /// đặt nó ⇒ thêm vai vào audience; màn có mục menu mà không vai nào thấy ⇒ thêm vào menu của audience; vai
    /// không có menu ⇒ dựng menu từ các màn của vai đó; trang đích thiếu ⇒ màn đầu tiên; ≥3 màn danh mục/báo
    /// cáo nằm phẳng ⇒ gom nhóm (cùng ngưỡng với audit POC, <see cref="PocNavGroups"/>).
    /// </summary>
    public static void Repair(UxBlueprint blueprint, IReadOnlyList<string> roles)
    {
        PlaceOrphanTasks(blueprint, roles);

        foreach (var screen in blueprint.Screens)
        {
            // Vai làm tác vụ phải vào được màn đặt tác vụ đó.
            foreach (var actor in screen.Actions
                         .Select(a => blueprint.Tasks.FirstOrDefault(t => SameId(t.Id, a.Task)))
                         .Where(t => t != null)
                         .SelectMany(t => t!.Actors)
                         .Where(a => roles.Count == 0 || roles.Any(r => Normalize(r) == Normalize(a))))
            {
                if (!screen.Audience.Any(a => Normalize(a) == Normalize(actor)))
                    screen.Audience.Add(roles.FirstOrDefault(r => Normalize(r) == Normalize(actor)) ?? actor);
            }
            if (screen.Audience.Count == 0)
                screen.Audience.AddRange(roles);
        }

        var allRoles = roles.Count > 0 ? roles.ToList() : blueprint.Screens.SelectMany(s => s.Audience).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var role in allRoles)
        {
            var nav = blueprint.Navigation.FirstOrDefault(n => Normalize(n.Role) == Normalize(role));
            if (nav == null)
            {
                nav = new UxNavigation { Role = role };
                blueprint.Navigation.Add(nav);
            }

            // Mục menu trỏ vào màn hình vai này không vào được ⇒ bỏ.
            nav.Items = PruneItems(nav.Items, blueprint.Screens, role);

            foreach (var screen in blueprint.Screens.Where(s => s.Entry == UxEntry.Nav && HasAudience(s, role)))
            {
                if (!ContainsScreen(nav.Items, screen.Name))
                    nav.Items.Add(new UxNavItem { Label = screen.Name, Screen = screen.Name });
            }

            GroupBatches(nav);

            if (MatchScreen(nav.Landing, blueprint.Screens) is not { } landing || !HasAudience(blueprint.Screens.First(s => s.Name == landing), role))
            {
                var first = FlattenScreens(nav.Items).FirstOrDefault();
                var dashboard = FlattenScreens(nav.Items)
                    .FirstOrDefault(n => blueprint.Screens.Any(s => s.Name == n && s.Pattern == UxPattern.Dashboard));
                nav.Landing = dashboard ?? first ?? string.Empty;
            }
        }

        // Menu của vai không còn trong danh sách vai (vai đã bị bỏ) ⇒ bỏ; menu không có mục nào (vai không vào
        // được màn nào) ⇒ bỏ — một trang đích "?" trong spec là một vai POC không biết mở gì cho.
        if (roles.Count > 0)
            blueprint.Navigation = blueprint.Navigation.Where(n => roles.Any(r => Normalize(r) == Normalize(n.Role))).ToList();
        blueprint.Navigation = blueprint.Navigation.Where(n => n.Items.Count > 0).ToList();

        foreach (var task in blueprint.Tasks)
            task.Placed = blueprint.Screens.Any(s => s.Actions.Any(a => SameId(a.Task, task.Id)));

        blueprint.LastTaskNumber = Math.Max(blueprint.LastTaskNumber, UxInventoryBuilder.NextNumber(blueprint.Tasks.Select(t => t.Id)) - 1);
        blueprint.LastScreenNumber = Math.Max(blueprint.LastScreenNumber, UxInventoryBuilder.NextNumber(blueprint.Screens.Select(s => s.Id)) - 1);
    }

    private static void PlaceOrphanTasks(UxBlueprint blueprint, IReadOnlyList<string> roles)
    {
        foreach (var task in blueprint.Tasks.Where(t => HomeOf(blueprint, t.Id) == null).ToList())
        {
            // Luôn có chỗ: CreateHost trả màn đầu tiên khi đã chạm trần số màn. Một tác vụ không được đặt làm
            // UxDesignGate.NeedsRedesign đúng mãi — mỗi lần bấm "Write Requirement" lại thiết kế lại.
            var host = FindHost(blueprint, task) ?? CreateHost(blueprint, task, roles);
            if (host == null)
                continue;

            host.Actions.Add(new UxAction
            {
                Task = task.Id,
                Label = Clip(task.Name, MaxLabelChars),
                Placement = DefaultPlacement(task.Kind),
                Prominence = task.Kind == UxTaskKind.Create ? UxProminence.Primary : UxProminence.Secondary,
                WhenState = task.Kind == UxTaskKind.Transition ? task.FromState : string.Empty,
                Form = task.Kind is UxTaskKind.Create or UxTaskKind.Edit ? UxFormMode.Modal : UxFormMode.None
            });
        }
    }

    // "Nhà" của tác vụ: tác vụ báo cáo về màn tổng quan/báo cáo; tác vụ của một đối tượng về màn NHÀ của đối
    // tượng đó (danh sách / master-data…), không có thì bất kỳ màn nào của đối tượng; tác vụ không gắn đối
    // tượng về màn mà người làm nó vào được.
    private static UxScreen? FindHost(UxBlueprint blueprint, UxTask task)
    {
        if (task.Kind == UxTaskKind.Report)
            return blueprint.Screens.FirstOrDefault(s => s.Pattern == UxPattern.Dashboard)
                   ?? blueprint.Screens.FirstOrDefault(s => s.Pattern == UxPattern.Report);

        if (task.Object.Length > 0)
        {
            var ofObject = blueprint.Screens.Where(s => Normalize(s.Object) == Normalize(task.Object)).ToList();
            return ofObject.FirstOrDefault(s => UxPattern.Home.Contains(s.Pattern)) ?? ofObject.FirstOrDefault();
        }

        return blueprint.Screens.FirstOrDefault(s => task.Actors.Any(a => HasAudience(s, a)) && s.Pattern != UxPattern.Dashboard)
               ?? blueprint.Screens.FirstOrDefault(s => s.Pattern != UxPattern.Dashboard);
    }

    private static UxScreen? CreateHost(UxBlueprint blueprint, UxTask task, IReadOnlyList<string> roles)
    {
        if (blueprint.Screens.Count >= MaxScreens)
            return blueprint.Screens.FirstOrDefault();

        var isReport = task.Kind == UxTaskKind.Report;
        var obj = blueprint.Objects.FirstOrDefault(o => Normalize(o.Name) == Normalize(task.Object));
        var name = isReport ? "Overview Dashboard"
            : obj != null ? (obj.IsCatalog ? $"{obj.Name} Catalog" : $"{obj.Name} List")
            : "Work Items";

        var existing = blueprint.Screens.FirstOrDefault(s => Normalize(s.Name) == Normalize(name));
        if (existing != null)
            return existing;

        blueprint.LastScreenNumber = Math.Max(blueprint.LastScreenNumber,
            UxInventoryBuilder.NextNumber(blueprint.Screens.Select(s => s.Id), blueprint.LastScreenNumber) - 1);
        var screen = new UxScreen
        {
            Id = UxInventoryBuilder.FormatId("S", ++blueprint.LastScreenNumber),
            Name = name,
            Pattern = isReport ? UxPattern.Dashboard : UxPattern.List,
            Object = obj?.Name ?? string.Empty,
            Purpose = isReport ? "Tổng quan số liệu" : obj != null ? $"Danh sách {obj.Name}" : string.Empty,
            Audience = task.Actors.Count > 0 ? task.Actors.ToList() : roles.ToList(),
            Entry = UxEntry.Nav,
            Rationale = "Bổ sung tự động: tác vụ chưa có chỗ trong bản thiết kế."
        };
        blueprint.Screens.Add(screen);
        return screen;
    }

    private static List<UxNavItem> PruneItems(List<UxNavItem> items, IReadOnlyList<UxScreen> screens, string role)
    {
        var result = new List<UxNavItem>();
        foreach (var item in items)
        {
            if (item.Screen.Length > 0)
            {
                var screen = screens.FirstOrDefault(s => s.Name == item.Screen);
                if (screen != null && HasAudience(screen, role))
                    result.Add(item);
                continue;
            }
            item.Children = PruneItems(item.Children, screens, role);
            if (item.Children.Count > 0)
                result.Add(item);
        }
        return result;
    }

    // ≥3 màn danh mục (hoặc ≥3 màn báo cáo) nằm phẳng ở menu gốc ⇒ gom vào MỘT nhóm. Cùng ngưỡng và cùng bộ
    // phân loại tên với audit POC, để bản thiết kế không bao giờ đẻ ra một menu mà audit sẽ bắt sửa.
    private static void GroupBatches(UxNavigation nav)
    {
        var topLeaves = nav.Items.Where(i => i.Screen.Length > 0).Select(i => i.Screen).ToList();
        foreach (var kind in PocNavGroups.KindsToGroup(topLeaves))
        {
            var members = nav.Items.Where(i => i.Screen.Length > 0 && PocNavGroups.Classify(i.Screen) == kind).ToList();
            var label = kind == PocNavGroupKind.Catalog ? "Catalogs" : "Reports"; // nhãn menu là nhãn giao diện ⇒ tiếng Anh
            var group = nav.Items.FirstOrDefault(i => i.Screen.Length == 0 && Normalize(i.Label) == Normalize(label));
            if (group == null)
            {
                group = new UxNavItem { Label = label };
                nav.Items.Insert(nav.Items.IndexOf(members[0]), group);
            }
            foreach (var member in members)
            {
                nav.Items.Remove(member);
                group.Children.Add(member);
            }
        }
    }

    // ==== tiện ích dùng chung ====

    /// <summary>Danh sách tên màn hình (lá) của một cây menu, theo thứ tự hiển thị.</summary>
    public static List<string> FlattenScreens(IEnumerable<UxNavItem> items)
        => items.SelectMany(i => i.Screen.Length > 0 ? new[] { i.Screen } : FlattenScreens(i.Children).ToArray()).ToList();

    private static bool ContainsScreen(IEnumerable<UxNavItem> items, string screen)
        => FlattenScreens(items).Any(s => s == screen);

    public static bool HasAudience(UxScreen screen, string role)
        => screen.Audience.Count == 0 || screen.Audience.Any(a => Normalize(a) == Normalize(role));

    public static bool SameId(string? a, string? b)
        => string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static UxTask? FindTask(string? reference, IReadOnlyList<UxTask> tasks)
    {
        var value = (reference ?? string.Empty).Trim();
        if (value.Length == 0)
            return null;
        return tasks.FirstOrDefault(t => SameId(t.Id, value))
               // Model hay chép "T-07 Thu hồi tủ" hoặc chính tên tác vụ.
               ?? tasks.FirstOrDefault(t => value.StartsWith(t.Id + " ", StringComparison.OrdinalIgnoreCase))
               ?? tasks.FirstOrDefault(t => Normalize(t.Name) == Normalize(value));
    }

    /// <summary>Kéo tên màn model gõ về đúng một màn đã có (khớp chính xác sau chuẩn hoá). Null khi không khớp.</summary>
    public static string? MatchScreen(string? proposed, IReadOnlyList<UxScreen> screens)
    {
        var key = Normalize(proposed);
        return key.Length == 0 ? null : screens.FirstOrDefault(s => Normalize(s.Name) == key)?.Name;
    }

    private static string MatchObject(string? proposed, IReadOnlyList<UxObject> objects)
    {
        var key = Normalize(proposed);
        return key.Length == 0 ? string.Empty : objects.FirstOrDefault(o => Normalize(o.Name) == key)?.Name ?? string.Empty;
    }

    private static List<string> MatchRoles(IEnumerable<string>? proposed, IReadOnlyList<string> roles)
    {
        var result = new List<string>();
        foreach (var raw in proposed ?? Enumerable.Empty<string>())
        {
            var value = Clip(OneLine(raw), MaxScreenNameChars);
            if (value.Length == 0)
                continue;
            var role = roles.Count == 0 ? value : roles.FirstOrDefault(r => Normalize(r) == Normalize(value));
            if (role != null && !result.Any(r => Normalize(r) == Normalize(role)))
                result.Add(role);
        }
        return result;
    }

    public static string NormalizePattern(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        if (value == "masterdata")
            value = UxPattern.MasterData;
        if (value is "listdetail" or "master-detail")
            value = UxPattern.ListDetail;
        return UxPattern.All.Contains(value) ? value : UxPattern.List;
    }

    /// <summary>
    /// Tên màn hình sạch: một dòng, bỏ chữ dẫn kiểu "Màn hình …"/"Trang …"/"… Screen" — tên là nhãn menu của
    /// bản demo, phần mô tả đã có ô <see cref="UxScreen.Purpose"/>.
    /// </summary>
    public static string CleanScreenName(string? raw)
    {
        var name = string.Join(' ', OneLine(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        name = LeadingScreenWordRegex().Replace(name, string.Empty);
        name = TrailingScreenWordRegex().Replace(name, string.Empty);
        return Clip(name.Trim(' ', '-', ':', '—'), MaxScreenNameChars).Trim();
    }

    [GeneratedRegex(@"^(màn\s+hình|trang|screen)\s*[:\-–—]?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingScreenWordRegex();

    [GeneratedRegex(@"\s+(screen|page)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingScreenWordRegex();
}
