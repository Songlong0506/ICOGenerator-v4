using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Deterministic self-check of a generated poc-demo.html, run by the Developer agent (AuditPocContent)
/// after all content/script calls. It catches exactly the defects that made reviewed POCs feel broken —
/// menu items whose click changes nothing, modal triggers pointing nowhere, half-wired CRUD, duplicate
/// ids and a still-empty logic script — so the agent fixes them before returning, instead of a human
/// discovering them in the demo. Checks are plain string/regex scans: the markup is machine-shaped
/// (shell template + Bootstrap sections), so no HTML parser dependency is warranted.
/// </summary>
public static class PocAudit
{
    // Ids owned by the shell (poc-template.html); feature content reusing one makes Bootstrap open the
    // wrong dialog or the shell script wire the wrong element. Must match the prompt's reserved list.
    private static readonly string[] ReservedIds =
        ["appShell", "userModal", "imprintModal", "toastHost", "sbToggle", "navUser", "navImprint"];

    public static string Run(string html) => Run(html, PocSpec.Empty);

    /// <summary>
    /// Audit with the feature-parity gate: <paramref name="spec"/> is the parsed AI Design Spec of
    /// this run, so the report can also say which spec screens the demo is missing and which
    /// business rules still need behaviour — the gap the wiring-only checks could never see (a POC
    /// covering half the spec used to audit "OK").
    /// </summary>
    public static string Run(string html, PocSpec spec)
    {
        var issues = new List<string>();
        var warnings = new List<string>();

        // The defect scans are about MARKUP, so they run on a copy with all HTML comments, <style>
        // and <script> blocks stripped: the shell template ships a large instructional comment and JS
        // comments full of example markup (data-crud-table="ENTITY", data-bs-target="#formModalId"…)
        // that would otherwise show up as fake entities and broken triggers, and ids mentioned inside
        // scripts aren't elements. <style> must go BEFORE <script>: a CSS comment in the shell
        // mentions the literal text "<script>", which would otherwise start a script match that
        // swallows everything up to the first real </script> — nav, sections and all. The checks that
        // live IN comments (the region markers, the seed placeholder) or in the script region still
        // use the raw html.
        var scan = Regex.Replace(html, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        scan = Regex.Replace(scan, "<style\\b.*?</style>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        scan = Regex.Replace(scan, "<script\\b.*?</script>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var navLeaves = NavLeafLabels(scan);
        var sections = SectionLabels(scan);
        CheckContentSeeded(html, issues);
        CheckNavAgainstSections(navLeaves, sections, issues, warnings);
        var ids = CheckIds(scan, issues);
        CheckModalTargets(scan, ids, issues);
        var crudEntities = CheckCrud(scan, issues, warnings);
        var scriptBody = PocTemplate.GetScriptBody(html);
        CheckScript(html, scriptBody, spec.Rules.Count, issues, warnings);
        var coveredScreens = CheckSpecCoverage(spec, navLeaves, sections, issues);

        return Render(issues, warnings, navLeaves, sections, crudEntities, scriptBody, spec, coveredScreens);
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

    private static HashSet<string> CheckIds(string html, List<string> issues)
    {
        var ids = Regex.Matches(html, "\\bid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        foreach (var group in ids.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            issues.Add(ReservedIds.Contains(group.Key, StringComparer.Ordinal)
                ? $"Id '{group.Key}' is reserved by the shell but is used again by the feature content — rename the feature element (e.g. '{group.Key}Form') or Bootstrap opens the wrong dialog."
                : $"Duplicate id '{group.Key}' ({group.Count()}x) — ids must be unique or modal triggers and labels hit the wrong element.");
        }
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    private static void CheckModalTargets(string html, HashSet<string> ids, List<string> issues)
    {
        var missing = Regex.Matches(html, "data-(?:bs-target|crud-modal)=\"#([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Where(id => !ids.Contains(id))
            .Distinct(StringComparer.Ordinal);
        foreach (var id in missing)
            issues.Add($"A trigger points at '#{id}' but no element with that id exists — the dialog can never open. Append the missing modal or fix the id.");
    }

    // CRUD wiring: a data-crud-table needs a matching data-crud-form (the engine's Edit — and Add
    // without data-crud-values — submit through it), and the forms' field names must cover the
    // table's data-field columns or saved records show empty cells.
    private static List<string> CheckCrud(string html, List<string> issues, List<string> warnings)
    {
        // All form blocks per entity. The contract is exactly ONE form per entity — the engine binds
        // the first — but field coverage is checked across all of them so a stray wrapper form doesn't
        // produce false mismatches; the duplication itself is reported separately.
        var formsByEntity = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Match form in Regex.Matches(html, "<form\\b[^>]*data-crud-form=\"([^\"]+)\"[^>]*>"))
        {
            var entity = form.Groups[1].Value;
            if (!formsByEntity.TryGetValue(entity, out var blocks))
                formsByEntity[entity] = blocks = new List<string>();
            blocks.Add(BlockAfter(html, form.Index, "</form>"));
        }

        foreach (var (entity, blocks) in formsByEntity.Where(kv => kv.Value.Count > 1))
            warnings.Add($"There are {blocks.Count} <form data-crud-form=\"{entity}\"> — keep exactly ONE per entity (the engine submits through the first, so an extra wrapper form around the table can hijack Add/Edit).");

        var tableEntities = new List<string>();
        foreach (Match table in Regex.Matches(html, "<table\\b[^>]*data-crud-table=\"([^\"]+)\"[^>]*>"))
        {
            var entity = table.Groups[1].Value;
            tableEntities.Add(entity);

            if (!formsByEntity.TryGetValue(entity, out var blocks))
            {
                issues.Add($"data-crud-table=\"{entity}\" has no matching <form data-crud-form=\"{entity}\"> — the engine's Add/Edit buttons cannot save. Add the form (usually inside a modal), or drop the data-crud-* attributes if this list is not meant to be user-edited.");
                continue;
            }

            var tableBlock = BlockAfter(html, table.Index, "</table>");
            var tableFields = Regex.Matches(tableBlock, "data-field=\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

            var formFields = new HashSet<string>(
                blocks.SelectMany(b => Regex.Matches(b, "\\bname=\"([^\"]+)\"").Select(m => m.Groups[1].Value)),
                StringComparer.Ordinal);

            var unmatched = tableFields.Where(f => !formFields.Contains(f)).ToList();
            if (unmatched.Count > 0)
                issues.Add($"CRUD '{entity}': no form control is named [{string.Join(", ", unmatched)}] although the table declares those data-field columns — records saved from the form leave those cells empty. Align name=\"…\" with data-field=\"…\".");
        }

        foreach (var add in Regex.Matches(html, "data-crud-add=\"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal))
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

        // Inline <script> inside the content region bypasses SetPocScript and is lost on content edits.
        if (TryGetContentRegion(html, out var content) && content.Contains("<script", StringComparison.OrdinalIgnoreCase))
            warnings.Add("The feature content carries an inline <script> — move that logic into SetPocScript (the dedicated POC_SCRIPT region) so it is kept when content is edited.");
    }

    private static string Render(
        List<string> issues, List<string> warnings,
        List<string> navLeaves, List<string> sections, List<string> crudEntities, string scriptBody,
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

        sb.Append($"Summary: {navLeaves.Count} menu leaves, {sections.Count} screens, ");
        if (spec.Screens.Count > 0)
            sb.Append($"spec coverage: {coveredScreens}/{spec.Screens.Count} spec screens, ");
        sb.Append(crudEntities.Count > 0 ? $"CRUD entities: {string.Join(", ", crudEntities.Distinct(StringComparer.Ordinal))}, " : "no CRUD entities, ");
        sb.Append(scriptBody.Length > 0 ? $"POC script: {scriptBody.Length} chars." : "POC script: empty.");
        return sb.ToString();
    }

    // Clickable sidebar leaves: nav-items inside <nav class="sidebar-nav"> that are NOT group headers
    // (headers carry the nav-chevron and only expand/collapse). The pinned User/Imprint items live in
    // .sidebar-foot, outside this <nav>, so they are naturally excluded.
    private static List<string> NavLeafLabels(string html)
    {
        var labels = new List<string>();
        var navStart = html.IndexOf("<nav class=\"sidebar-nav\">", StringComparison.Ordinal);
        if (navStart < 0)
            return labels;
        var navEnd = html.IndexOf("</nav>", navStart, StringComparison.Ordinal);
        if (navEnd < 0)
            return labels;
        var nav = html[navStart..navEnd];

        var itemStarts = Regex.Matches(nav, "<div class=\"nav-item[\" ]");
        for (var i = 0; i < itemStarts.Count; i++)
        {
            var start = itemStarts[i].Index;
            var end = i + 1 < itemStarts.Count ? itemStarts[i + 1].Index : nav.Length;
            var block = nav[start..end];
            if (block.Contains("nav-chevron", StringComparison.Ordinal))
                continue; // group header

            var label = Regex.Match(block, "<span class=\"nav-label\">(.*?)</span>", RegexOptions.Singleline);
            if (!label.Success)
                continue;
            var text = WebUtility.HtmlDecode(label.Groups[1].Value).Trim();
            if (text.Length > 0)
                labels.Add(text);
        }
        return labels;
    }

    private static List<string> SectionLabels(string html)
    {
        var labels = new List<string>();
        foreach (Match tag in Regex.Matches(html, "<section\\b[^>]*>"))
        {
            if (!tag.Value.Contains("page-view", StringComparison.Ordinal))
                continue;
            var view = Regex.Match(tag.Value, "data-view=\"([^\"]*)\"");
            if (!view.Success)
                continue;
            var text = WebUtility.HtmlDecode(view.Groups[1].Value).Trim();
            if (text.Length > 0)
                labels.Add(text);
        }
        return labels;
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

    private static string BlockAfter(string html, int startIdx, string closeTag)
    {
        var close = html.IndexOf(closeTag, startIdx, StringComparison.OrdinalIgnoreCase);
        return close < 0 ? html[startIdx..] : html[startIdx..close];
    }

    // Same normalization the shell's view routing applies (viewKey): labels match case-insensitively.
    private static string Key(string label) => label.Trim().ToLowerInvariant();
}
