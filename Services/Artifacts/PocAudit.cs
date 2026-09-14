using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace ICOGenerator.Services.Artifacts;

/// <summary>Kết quả một lượt audit tĩnh: report render sẵn cho agent + phần cấu trúc để lưu lại cho trang review.</summary>
public sealed record PocAuditOutcome(
    string Report,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Warnings,
    int SpecScreens,
    int CoveredScreens);

/// <summary>
/// Deterministic self-check of a generated poc-demo.html, run by the Developer agent (AuditPocContent)
/// after all content/script calls. It catches exactly the defects that made reviewed POCs feel broken —
/// menu items whose click changes nothing, modal triggers pointing nowhere, half-wired CRUD, duplicate
/// ids and a still-empty logic script — so the agent fixes them before returning, instead of a human
/// discovering them in the demo. Markup is queried through a real DOM (<see cref="PocDom"/>) with CSS
/// selectors — see that class for why the old "machine-shaped markup, no parser needed" argument did not
/// survive contact with LLM-written feature content.
/// </summary>
public static partial class PocAudit
{
    // Ids owned by the shell (poc-template.html); feature content reusing one makes Bootstrap open the
    // wrong dialog or the shell script wire the wrong element. Must match the prompt's reserved list.
    private static readonly string[] ReservedIds =
        ["appShell", "userModal", "imprintModal", "toastHost", "sbToggle", "navUser", "navUserRole", "navImprint", "viewAs", "viewAsList"];

    /// <summary>Một mục menu BẤM ĐƯỢC của sidebar: nhãn, vai được thấy, và nhãn nhóm chứa nó (null = nằm thẳng ở menu gốc).</summary>
    private sealed record PocNavLeaf(string Label, IReadOnlyList<string> Roles, string? Group);

    public static string Run(string html) => Run(html, PocSpec.Empty);

    /// <summary>
    /// Audit with the feature-parity gate: <paramref name="spec"/> is the parsed AI Design Spec of
    /// this run, so the report can also say which spec screens the demo is missing and which
    /// business rules still need behaviour — the gap the wiring-only checks could never see (a POC
    /// covering half the spec used to audit "OK").
    /// </summary>
    public static string Run(string html, PocSpec spec) => RunDetailed(html, spec).Report;

    /// <summary>
    /// Như <see cref="Run(string, PocSpec)"/> nhưng trả thêm danh sách issue/warning CÓ CẤU TRÚC và số
    /// màn hình spec đã phủ — để <c>AuditPocContent</c> lưu lại kết quả vòng kiểm CUỐI cho trang POC
    /// Review (người review cần biết máy đã kiểm gì và còn gì chưa đạt, không chỉ agent cần biết).
    /// <paramref name="sampleData"/> (tùy chọn) bật thêm tầng <see cref="PocSampleDataCheck"/>: dữ liệu
    /// mẫu có dùng đúng tài liệu người dùng gửi không, và UI có đúng ngôn ngữ của spec không.
    /// </summary>
    public static PocAuditOutcome RunDetailed(string html, PocSpec spec, PocSampleDataContext? sampleData = null)
    {
        var issues = new List<string>();
        var warnings = new List<string>();

        // Các phép kiểm MARKUP chạy trên DOM. Bản cũ phải xóa comment/<style>/<script> bằng regex trước
        // khi quét, vì khung vỏ chở một comment hướng dẫn dài và các comment JS đầy markup ví dụ
        // (data-crud-table="ENTITY", data-bs-target="#formModalId"…) sẽ bị tính thành entity giả và
        // trigger hỏng, còn id nhắc trong script thì không phải phần tử. Trong DOM chuyện đó tự đúng:
        // comment là node riêng, nội dung script/style là văn bản. Các phép kiểm nằm TRONG comment
        // (mốc vùng, placeholder seed) hay trong vùng script vẫn đọc html thô.
        var dom = PocDom.Parse(html);

        var navEntries = NavLeaves(dom);
        var navLeaves = navEntries.Select(l => l.Label).ToList();
        var sections = SectionLabels(dom);
        CheckContentSeeded(html, issues);
        CheckNavAgainstSections(navLeaves, sections, issues, warnings);
        CheckNavGrouping(navEntries, issues);
        var ids = CheckIds(dom, issues);
        CheckModalTargets(dom, ids, issues);
        var crudEntities = CheckCrud(dom, issues, warnings);
        var scriptBody = PocTemplate.GetScriptBody(html);
        CheckScript(html, scriptBody, spec.Rules.Count, issues, warnings);
        // Worked examples có trong spec ⇒ POC PHẢI định nghĩa window.pocWorkedExamples() để oracle độc lập
        // đối chiếu (runtime check chạy nó). Thiếu hàm = mất tầng kiểm con số người dùng đã chốt.
        if (spec.WorkedExamples.Count > 0 && scriptBody.Length > 0 && !scriptBody.Contains("pocWorkedExamples", StringComparison.Ordinal))
            issues.Add($"The POC script does not define window.pocWorkedExamples() although the AI Design Spec declares {spec.WorkedExamples.Count} worked example(s) — define it (globally) to return one entry per example: [{{ ref: 'WE-1', computed: <value computed by CALLING the demo's own logic for that example's inputs> }}, …]. The audit runs it in a headless browser and checks each computed value against the user-confirmed expected result.");
        var coveredScreens = CheckSpecCoverage(spec, navLeaves, sections, issues);
        var declaredRoles = CheckRoles(dom, spec, issues, warnings);

        // Dữ liệu mẫu + ngôn ngữ UI: chạy trên vùng POC_CONTENT của bản GỐC (không phải bản đã strip
        // script/style ở trên — check này tự cắt vùng của nó). Không có bối cảnh ⇒ Empty, báo cáo y như cũ.
        var sample = PocSampleDataCheck.Inspect(html, sampleData);
        issues.AddRange(sample.Issues);
        warnings.AddRange(sample.Warnings);

        var report = Render(issues, warnings, navLeaves, sections, crudEntities, scriptBody, declaredRoles, spec, coveredScreens);
        return new PocAuditOutcome(report, issues, warnings, spec.Screens.Count, coveredScreens);
    }

    // Feature-parity gate: every screen the AI Design Spec declares (§ Screens To Generate) must
    // exist in the demo as a page-view section or at least a menu leaf (a leaf whose section is
    // missing is already an issue from CheckNavAgainstSections). Matching is fuzzy both ways so
    // "Màn hình Đăng nhập" in the spec still pairs with a section labelled "Đăng nhập". Returns how
    // many spec screens were found, for the summary line.
    private static int CheckSpecCoverage(PocSpec spec, List<string> navLeaves, List<string> sections, List<string> issues)
    {
        if (spec.Screens.Count == 0)
            return 0;

        var labels = sections.Concat(navLeaves).ToList();
        var covered = 0;
        foreach (var screen in spec.Screens)
        {
            if (labels.Any(label => PocSpec.Matches(screen, label)))
            {
                covered++;
                continue;
            }
            issues.Add($"Spec screen '{screen}' (AI Design Spec § Screens To Generate) has no matching menu item or page-view section — that feature is missing from the demo. Append it (<section class=\"page-view\" data-view=\"{screen}\"> plus a menu entry), or rename an existing screen if it is the same one under a different name.");
        }
        return covered;
    }

    private static void CheckContentSeeded(string html, List<string> issues)
    {
        if (html.Contains(PocTemplate.Placeholder, StringComparison.Ordinal))
            issues.Add("The POC content region still holds the seed placeholder — SetPocContent has not written the feature UI yet.");
    }

    // Every clickable menu leaf (top-level leaf or sub-item; group headers only expand) must have a
    // page-view section with the same data-view label, or clicking it changes nothing but the breadcrumb.
    private static void CheckNavAgainstSections(
        List<string> navLeaves, List<string> sections, List<string> issues, List<string> warnings)
    {
        var sectionKeys = new HashSet<string>(sections.Select(Key));
        foreach (var leaf in navLeaves.Where(l => !sectionKeys.Contains(Key(l))))
            issues.Add($"Menu item '{leaf}' has no <section class=\"page-view\" data-view=\"{leaf}\"> — clicking it will not change the page. Append the missing section or rename one to match.");

        var leafKeys = new HashSet<string>(navLeaves.Select(Key));
        foreach (var s in sections.Where(s => !leafKeys.Contains(Key(s))))
            warnings.Add($"Section data-view=\"{s}\" is not opened by any menu item — fine only if the POC script navigates to it (pocNavigate('{s}')); otherwise it is unreachable.");
    }

    // GOM NHÓM MENU: các màn hình sinh ra theo LÔ (danh mục "<tên> Catalog" từ bảng đối tượng, báo cáo
    // "<tên> Report" từ bảng báo cáo) phải nằm trong MỘT mục xổ xuống, không rải phẳng ra menu gốc.
    // Một dự án nhân sự bình thường có 5–8 danh mục: để phẳng thì phần nghiệp vụ thật của sidebar bị
    // đẩy xuống dưới một dãy màn CRUD giống hệt nhau, và người xem demo phải cuộn qua hết mới tới được
    // luồng chính. Ngưỡng + cách phân loại ở PocNavGroups (cố ý hẹp: bỏ sót thì im, bắt nhầm thì ồn).
    private static void CheckNavGrouping(List<PocNavLeaf> leaves, List<string> issues)
    {
        foreach (var kind in PocNavGroups.KindsToGroup(leaves.Select(l => l.Label)))
        {
            var members = leaves.Where(l => PocNavGroups.Classify(l.Label) == kind).ToList();
            var loose = members.Where(l => l.Group == null).Select(l => l.Label).ToList();
            var groups = members.Where(l => l.Group != null)
                .Select(l => l.Group!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Đã gom đủ vào đúng MỘT nhóm ⇒ không nói gì.
            if (loose.Count == 0 && groups.Count <= 1)
                continue;

            var what = PocNavGroups.Describe(kind);
            var detail = loose.Count > 0
                ? $"{loose.Count} mục còn nằm thẳng ở menu gốc ({string.Join(", ", loose.Select(l => $"'{l}'"))})"
                : $"chúng đang bị chia ra {groups.Count} nhóm khác nhau ({string.Join(", ", groups.Select(g => $"'{g}'"))})";

            issues.Add(
                $"Bản demo có {members.Count} màn hình {what} nhưng {detail} — gom TẤT CẢ vào ĐÚNG MỘT mục menu xổ xuống "
                + $"(một entry navItems có \"children\" là các màn hình đó, nhãn nhóm đặt theo ngôn ngữ UI của spec, vd {PocNavGroups.SampleLabel(kind)}). "
                + "Sửa bằng cách gọi lại SetPocContent với navItems mới (giữ nguyên content) — nhãn mục CON phải giữ NGUYÊN VĂN tên màn hình để vẫn khớp data-view của section, "
                + "và tiêu đề nhóm KHÔNG phải một màn hình nên đừng tạo section cho nó. "
                + $"Để phẳng thì {members.Count} màn hình giống hệt nhau đẩy phần nghiệp vụ chính của sidebar xuống dưới, đúng thứ người xem demo mở lên để xem.");
        }
    }

    private static HashSet<string> CheckIds(IHtmlDocument dom, List<string> issues)
    {
        // [id] chỉ trả về PHẦN TỬ có id thật. Mẫu cũ (\bid="…") còn khớp cả data-id="…" (dấu '-' là một
        // ranh giới từ) và mọi chuỗi id="…" nằm trong literal JavaScript — hai nguồn "id trùng" ma.
        var ids = dom.QuerySelectorAll("[id]")
            .Select(x => x.Id ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToList();
        foreach (var group in ids.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            issues.Add(ReservedIds.Contains(group.Key, StringComparer.Ordinal)
                ? $"Id '{group.Key}' is reserved by the shell but is used again by the feature content — rename the feature element (e.g. '{group.Key}Form') or Bootstrap opens the wrong dialog."
                : $"Duplicate id '{group.Key}' ({group.Count()}x) — ids must be unique or modal triggers and labels hit the wrong element.");
        }
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    private static void CheckModalTargets(IHtmlDocument dom, HashSet<string> ids, List<string> issues)
    {
        var missing = dom.QuerySelectorAll("[data-bs-target], [data-crud-modal]")
            .Select(x => x.GetAttribute("data-bs-target") ?? x.GetAttribute("data-crud-modal") ?? string.Empty)
            .Where(x => x.StartsWith('#'))
            .Select(x => x[1..])
            .Where(id => id.Length > 0 && !ids.Contains(id))
            .Distinct(StringComparer.Ordinal);
        foreach (var id in missing)
            issues.Add($"A trigger points at '#{id}' but no element with that id exists — the dialog can never open. Append the missing modal or fix the id.");
    }

    // CRUD wiring: a data-crud-table needs a matching data-crud-form (the engine's Edit — and Add
    // without data-crud-values — submit through it), and the forms' field names must cover the
    // table's data-field columns or saved records show empty cells.
    private static List<string> CheckCrud(IHtmlDocument dom, List<string> issues, List<string> warnings)
    {
        // All form elements per entity. The contract is exactly ONE form per entity — the engine binds
        // the first — but field coverage is checked across all of them so a stray wrapper form doesn't
        // produce false mismatches; the duplication itself is reported separately.
        var formsByEntity = dom.QuerySelectorAll("form[data-crud-form]")
            .GroupBy(x => x.GetAttribute("data-crud-form") ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var (entity, forms) in formsByEntity.Where(kv => kv.Value.Count > 1))
            warnings.Add($"There are {forms.Count} <form data-crud-form=\"{entity}\"> — keep exactly ONE per entity (the engine submits through the first, so an extra wrapper form around the table can hijack Add/Edit).");

        var tableEntities = new List<string>();
        foreach (var table in dom.QuerySelectorAll("table[data-crud-table]"))
        {
            var entity = table.GetAttribute("data-crud-table") ?? string.Empty;
            tableEntities.Add(entity);

            if (!formsByEntity.TryGetValue(entity, out var forms))
            {
                issues.Add($"data-crud-table=\"{entity}\" has no matching <form data-crud-form=\"{entity}\"> — the engine's Add/Edit buttons cannot save. Add the form (usually inside a modal), or drop the data-crud-* attributes if this list is not meant to be user-edited.");
                continue;
            }

            // Phạm vi là CÂY CON của đúng bảng này. Bản cũ cắt chuỗi từ thẻ mở tới "</table>" gần nhất
            // (BlockAfter), nên một bảng lồng trong bảng làm phần đuôi của bảng ngoài bị bỏ sót.
            var tableFields = table.QuerySelectorAll("[data-field]")
                .Select(x => x.GetAttribute("data-field") ?? string.Empty)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var formFields = new HashSet<string>(
                forms.SelectMany(f => f.QuerySelectorAll("[name]"))
                     .Select(x => x.GetAttribute("name") ?? string.Empty)
                     .Where(x => x.Length > 0),
                StringComparer.Ordinal);

            var unmatched = tableFields.Where(f => !formFields.Contains(f)).ToList();
            if (unmatched.Count > 0)
                issues.Add($"CRUD '{entity}': no form control is named [{string.Join(", ", unmatched)}] although the table declares those data-field columns — records saved from the form leave those cells empty. Align name=\"…\" with data-field=\"…\".");
        }

        var addEntities = dom.QuerySelectorAll("[data-crud-add]")
            .Select(x => x.GetAttribute("data-crud-add") ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal);

        foreach (var add in addEntities)
            if (!tableEntities.Contains(add) && !formsByEntity.ContainsKey(add))
                warnings.Add($"data-crud-add=\"{add}\" has neither a data-crud-table nor a data-crud-form for that entity — the button adds records nothing displays.");

        return tableEntities;
    }

    private static void CheckScript(string html, string scriptBody, int specRuleCount, List<string> issues, List<string> warnings)
    {
        if (scriptBody.Length == 0)
        {
            // With a parsed spec this is a hard ISSUE: rules are declared, so an empty script means a
            // static POC by definition. Without one (old spec / audit run standalone) it stays the
            // benefit-of-the-doubt warning.
            if (specRuleCount > 0)
                issues.Add($"The POC logic script (POC_SCRIPT region) is empty although the AI Design Spec declares {specRuleCount} business rule(s) — the demo would be static screens. Implement them with SetPocScript: compute derived values from the data on screen, validate live while typing, drive the status/sign transitions on click.");
            else
                warnings.Add("The POC logic script (POC_SCRIPT region) is still empty — if the AI Design Spec defines business rules (computed totals/averages/ratings, sign or approval flows, role-based screens), implement them with SetPocScript so the demo behaves instead of only showing static screens.");
        }

        // Business rules must be VERIFIABLE, not just claimed: the prompt requires the script to define
        // window.pocSelfTest() with one assertion per BR-n. The runtime check executes it in a headless
        // browser and turns each failing rule into a concrete issue; without the function the rules are
        // back to being taken on the agent's word.
        if (specRuleCount > 0 && scriptBody.Length > 0 && !scriptBody.Contains("pocSelfTest", StringComparison.Ordinal))
            issues.Add($"The POC script does not define window.pocSelfTest() although the AI Design Spec declares {specRuleCount} business rule(s) — define it (globally) to return one entry per rule: [{{ rule: 'BR-1', pass: <boolean computed by CALLING the demo's own logic on the seed data>, detail: '<expected vs actual>' }}, …]. The audit runs it in a headless browser and reports failing rules.");

        // Inline <script> inside the content region bypasses SetPocScript and is lost on content edits.
        if (TryGetContentRegion(html, out var content) && content.Contains("<script", StringComparison.OrdinalIgnoreCase))
            warnings.Add("The feature content carries an inline <script> — move that logic into SetPocScript (the dedicated POC_SCRIPT region) so it is kept when content is edited.");
    }

    private static string Render(
        List<string> issues, List<string> warnings,
        List<string> navLeaves, List<string> sections, List<string> crudEntities, string scriptBody, List<string> declaredRoles,
        PocSpec spec, int coveredScreens)
    {
        var sb = new StringBuilder();
        sb.AppendLine(issues.Count == 0 && warnings.Count == 0
            ? "POC audit: OK — no issues found."
            : $"POC audit: {issues.Count} issue(s) to fix, {warnings.Count} warning(s).");

        if (issues.Count > 0)
        {
            sb.AppendLine("ISSUES (fix these before returning your final result):");
            for (var i = 0; i < issues.Count; i++)
                sb.AppendLine($"{i + 1}. {issues[i]}");
        }
        if (warnings.Count > 0)
        {
            sb.AppendLine("WARNINGS (fix if unintended):");
            for (var i = 0; i < warnings.Count; i++)
                sb.AppendLine($"{i + 1}. {warnings[i]}");
        }

        // Rule behaviour cannot be verified by a string scan, so the rules are echoed as a checklist
        // right when the agent is fixing things — each one must demonstrably run in the demo.
        if (spec.Rules.Count > 0)
        {
            sb.AppendLine("BUSINESS RULES from the AI Design Spec — verify EACH ONE actually behaves in the demo (computed live from the data on screen, validated while typing, state changed on click); implement any missing one via SetPocScript/AppendPocScript before returning:");
            for (var i = 0; i < spec.Rules.Count; i++)
                sb.AppendLine($"{i + 1}. {spec.Rules[i]}");
        }

        // Worked examples are the INDEPENDENT oracle: expected values the user confirmed. window.pocWorkedExamples()
        // must reproduce each by CALLING the demo's real logic — never hard-code the expected number.
        if (spec.WorkedExamples.Count > 0)
        {
            sb.AppendLine("WORKED EXAMPLES from the AI Design Spec — window.pocWorkedExamples() must return { ref, computed } for EACH, where 'computed' is produced by calling the demo's own business logic on that example's inputs (NOT the expected number hard-coded). The runtime check compares each computed value to the user-confirmed expected:");
            foreach (var we in spec.WorkedExamples)
                sb.AppendLine($"- {we.Ref}{(string.IsNullOrWhiteSpace(we.RuleRef) ? "" : $" ({we.RuleRef})")}: {we.Description} => expected {we.Expected}");
        }

        // Acceptance criteria are the USER'S OWN wording, copied verbatim from the approved Product Brief
        // ("Hoàn thành khi: …"). Unlike the rules above — which the BA phrased for machines — these are
        // what a business reviewer reads to say "đạt / chưa đạt", so the agent gets them in front of it
        // while it is still fixing things rather than discovering them at the acceptance meeting.
        if (spec.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("ACCEPTANCE CRITERIA from the AI Design Spec (§ 14) — these are the user's OWN acceptance sentences from the approved Product Brief, and the demo is signed off against them. Walk each one on the demo and make sure it is literally true there:");
            foreach (var ac in spec.AcceptanceCriteria)
                sb.AppendLine($"- {ac.Ref}{(string.IsNullOrWhiteSpace(ac.Feature) ? "" : $" ({ac.Feature})")}: {ac.Text}");
        }

        sb.Append($"Summary: {navLeaves.Count} menu leaves, {sections.Count} screens, ");
        sb.Append(declaredRoles.Count > 0
            ? $"VIEW AS roles: {string.Join(" / ", declaredRoles)} (first = default), "
            : "no VIEW AS roles, ");
        if (spec.Screens.Count > 0)
            sb.Append($"spec coverage: {coveredScreens}/{spec.Screens.Count} spec screens, ");
        sb.Append(crudEntities.Count > 0 ? $"CRUD entities: {string.Join(", ", crudEntities.Distinct(StringComparer.Ordinal))}, " : "no CRUD entities, ");
        sb.Append(scriptBody.Length > 0 ? $"POC script: {scriptBody.Length} chars." : "POC script: empty.");
        return sb.ToString();
    }

    // VAI (khối VIEW AS ở cuối sidebar) — chỗ NGƯỜI XEM DEMO đổi vai, thay cho một màn đăng nhập giả.
    // Ba lớp lỗi được soát ở đây, đều là loại "nhìn ảnh chụp không thấy": spec có nhiều vai mà demo
    // không cho đổi vai; data-roles gõ sai tên vai (mục menu/màn hình biến mất với MỌI vai); và một vai
    // được khai báo nhưng không còn mục menu nào để mở. Trả về danh sách vai đã khai báo cho phần Summary.
    private static List<string> CheckRoles(IHtmlDocument dom, PocSpec spec, List<string> issues, List<string> warnings)
    {
        var declared = DeclaredRoles(dom);
        var declaredKeys = new HashSet<string>(declared.Select(PocRole.Key), StringComparer.Ordinal);

        if (declared.Count == 0 && spec.Roles.Count > 1)
            issues.Add($"The AI Design Spec's Permission Matrix names {spec.Roles.Count} roles ({string.Join(", ", spec.Roles)}) but the demo declares none, so nobody can see what each role gets. Re-issue SetPocContent with roles: [{string.Join(", ", spec.Roles.Select(r => $"\"{r}\""))}] — that builds the VIEW AS switcher at the bottom of the sidebar — and tag the menu entries/sections that are role-specific with data-roles. Do NOT build a login screen for this: a POC has no backend, so the gate only hides the behaviour the demo exists to show.");

        // Tên vai lệch giữa spec và demo: không phải lỗi cứng (spec có thể gộp/đặt tên khác) nhưng người
        // nghiệm thu đối chiếu theo tên, nên nói ra.
        if (declared.Count > 0)
        {
            foreach (var specRole in spec.Roles.Where(r => !declaredKeys.Contains(PocRole.Key(r))))
                warnings.Add($"Role '{specRole}' of the spec's Permission Matrix is not one of the VIEW AS roles ({string.Join(", ", declared)}) — rename the switcher entry or add it, so the reviewer can check that role's screens.");
        }

        var leaves = NavLeaves(dom);
        var tagged = leaves.Select(l => (Kind: "menu item", Label: l.Label, Roles: l.Roles))
            .Concat(SectionRoles(dom).Select(x => (Kind: "screen", Label: x.Label, Roles: x.Roles)))
            .Where(x => x.Roles.Count > 0)
            .ToList();

        if (declared.Count > 0)
        {
            foreach (var entry in tagged)
            {
                var unknown = entry.Roles.Where(r => !declaredKeys.Contains(PocRole.Key(r))).ToList();
                if (unknown.Count > 0)
                    issues.Add($"The {entry.Kind} '{entry.Label}' declares data-roles=\"{string.Join(",", entry.Roles)}\" but {string.Join(", ", unknown.Select(u => $"'{u}'"))} is not a VIEW AS role ({string.Join(", ", declared)}) — with a name no role matches it is hidden for EVERYONE. Fix the spelling or add that role to SetPocContent's roles.");
            }

            // Một vai không mở được màn nào = chọn vai đó thì sidebar trống. Chỉ xét khi MỌI mục menu đều
            // đã gắn data-roles: mục không gắn là mục mọi vai đều thấy, nên vai đó vẫn có việc để làm.
            if (leaves.Count > 0 && leaves.All(l => l.Roles.Count > 0))
            {
                foreach (var role in declared)
                {
                    if (!leaves.Any(l => l.Roles.Any(r => PocRole.Key(r) == PocRole.Key(role))))
                        issues.Add($"No menu item is visible to the VIEW AS role '{role}' — switching to it leaves the reviewer with an empty sidebar. Give that role at least one screen (add it to a menu entry's data-roles) or drop it from the roles list.");
                }
            }
        }

        // Cửa gác giả: một màn Login/Đăng nhập KHÔNG có trong spec chỉ làm người xem demo phải bấm thêm
        // một lượt trước khi thấy nghiệp vụ — và giấu luôn phần còn lại khỏi các cổng tự kiểm.
        var login = SectionRoles(dom).FirstOrDefault(x => LoginScreenRegex().IsMatch(x.Label));
        if (login.Label is { Length: > 0 } && !spec.Screens.Any(sc => PocSpec.Matches(sc, login.Label)))
            warnings.Add($"Screen '{login.Label}' looks like a login gate the spec never asked for. A POC has no backend, so it only delays the business behaviour — drop it and let the VIEW AS switcher at the bottom of the sidebar carry the persona instead.");

        return declared;
    }

    // Các nút của khối VIEW AS: <button class="view-as-item" data-role="Manager">. Quét trên bản đã bỏ
    // comment/script nên ví dụ trong comment của template không bị tính là vai thật.
    private static List<string> DeclaredRoles(IHtmlDocument dom)
    {
        var roles = new List<string>();
        foreach (var button in dom.QuerySelectorAll("button.view-as-item[data-role]"))
        {
            var label = (button.GetAttribute("data-role") ?? string.Empty).Trim();
            if (label.Length > 0 && !roles.Any(r => PocRole.Key(r) == PocRole.Key(label)))
                roles.Add(label);
        }
        return roles;
    }

    private static List<(string Label, IReadOnlyList<string> Roles)> SectionRoles(IHtmlDocument dom) =>
        PageViews(dom).Select(x => (x.Label, RolesOf(x.Element))).ToList();

    private static List<string> SectionLabels(IHtmlDocument dom) =>
        PageViews(dom).Select(x => x.Label).ToList();

    /// <summary>
    /// Các section màn hình. Selector đòi ĐỒNG THỜI lớp page-view và thuộc tính data-view, nên không còn
    /// phụ thuộc vào việc hai thứ đó nằm theo thứ tự nào trong thẻ mở — mẫu cũ khớp cả thẻ rồi tìm chuỗi
    /// "page-view" trong đó, nên một section có <c>data-view="page-view-config"</c> cũng lọt.
    /// </summary>
    private static IEnumerable<(IElement Element, string Label)> PageViews(IHtmlDocument dom) =>
        dom.QuerySelectorAll("section.page-view[data-view]")
            .Select(x => (Element: x, Label: (x.GetAttribute("data-view") ?? string.Empty).Trim()))
            .Where(x => x.Label.Length > 0);

    private static IReadOnlyList<string> RolesOf(IElement element)
    {
        var raw = element.GetAttribute("data-roles");
        return raw == null ? [] : PocRole.SplitCsv(raw);
    }

    /// <summary>
    /// Mục menu BẤM ĐƯỢC của sidebar: các <c>.nav-item</c> trong <c>nav.sidebar-nav</c> không phải tiêu đề
    /// nhóm (tiêu đề mang <c>.nav-chevron</c> và chỉ đóng/mở). Mục User/Imprint ghim ở <c>.sidebar-foot</c>
    /// nằm ngoài <c>&lt;nav&gt;</c> nên tự động không lọt vào.
    /// <para>
    /// Quan hệ mục–nhóm đọc bằng <c>Closest(".nav-sub")</c>. Bản cũ phải tự dựng lại quan hệ đó từ VỊ TRÍ
    /// KÝ TỰ: tìm mọi <c>&lt;div class="nav-sub"</c>, đếm độ sâu <c>&lt;div&gt;</c> để tìm thẻ đóng
    /// (<c>MatchingDivEnd</c> — một bộ phân tích HTML tự viết, và nó đếm cả chữ "&lt;div" nằm trong giá trị
    /// thuộc tính hay nội dung chữ), rồi xét mục nào có chỉ số nằm giữa hai mốc.
    /// </para>
    /// </summary>
    private static List<PocNavLeaf> NavLeaves(IHtmlDocument dom)
    {
        var nav = dom.QuerySelector("nav.sidebar-nav");
        if (nav == null)
            return [];

        var leaves = new List<PocNavLeaf>();
        foreach (var item in nav.QuerySelectorAll(".nav-item"))
        {
            if (item.QuerySelector(".nav-chevron") != null)
                continue; // tiêu đề nhóm

            var label = item.QuerySelector(".nav-label")?.TextContent.Trim();
            if (string.IsNullOrEmpty(label))
                continue;

            leaves.Add(new PocNavLeaf(label, RolesOf(item), GroupOf(item)));
        }
        return leaves;
    }

    /// <summary>
    /// Nhãn nhóm chứa mục này, hoặc <c>null</c> nếu nó nằm trần ở menu gốc. Nhóm là phần tử
    /// <c>.nav-sub</c> gần nhất phía trên; nhãn của nhóm nằm ở mục menu ngay TRƯỚC phần tử đó — đúng hình
    /// dạng mà <see cref="PocTemplate"/> dựng ra. Không đọc được nhãn ⇒ chuỗi rỗng: mục vẫn được tính là
    /// "đã ở trong một nhóm", chỉ là nhóm không tên (giữ nguyên hành vi cũ).
    /// </summary>
    private static string? GroupOf(IElement item)
    {
        var sub = item.Closest(".nav-sub");
        if (sub == null)
            return null;

        var header = sub.PreviousElementSibling;
        while (header != null && header.QuerySelector(".nav-label") == null)
            header = header.PreviousElementSibling;

        return header?.QuerySelector(".nav-label")?.TextContent.Trim() ?? string.Empty;
    }

    private static bool TryGetContentRegion(string html, out string content)
    {
        content = string.Empty;
        var start = html.IndexOf(PocTemplate.StartMarker, StringComparison.Ordinal);
        var end = html.IndexOf(PocTemplate.EndMarker, StringComparison.Ordinal);
        if (start < 0 || end <= start)
            return false;
        content = html[(start + PocTemplate.StartMarker.Length)..end];
        return true;
    }

    // Same normalization the shell's view routing applies (viewKey): labels match case-insensitively.
    private static string Key(string label) => label.Trim().ToLowerInvariant();

    // Nhãn màn hình trông như một cửa đăng nhập ("Login", "Đăng nhập", "Sign in").
    [GeneratedRegex("^\\s*(?:login|log in|sign[- ]?in|đăng nhập)\\b", RegexOptions.IgnoreCase)]
    private static partial Regex LoginScreenRegex();
}
