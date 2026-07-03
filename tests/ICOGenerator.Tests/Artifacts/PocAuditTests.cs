using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

public class PocAuditTests
{
    // Minimal generated-file shape carrying the exact anchors PocAudit scans: the sidebar <nav>,
    // the pinned User/Imprint items OUTSIDE it (must never count as menu leaves), the content
    // region, the shell modals and the POC_SCRIPT region.
    private static string Doc(string nav, string content, string script = "var state = 1;")
    {
        var doc =
            "<html><body>\n" +
            "<aside class=\"sidebar\">\n<nav class=\"sidebar-nav\">\n" + nav + "\n</nav>\n" +
            "<div class=\"sidebar-foot\">\n" +
            "  <div class=\"nav-item\" id=\"navUser\" data-bs-toggle=\"modal\" data-bs-target=\"#userModal\"><span class=\"nav-label\">EXTERNAL User Name</span></div>\n" +
            "  <div class=\"nav-item\" id=\"navImprint\" data-bs-toggle=\"modal\" data-bs-target=\"#imprintModal\"><span class=\"nav-label\">Imprint</span></div>\n" +
            "</div>\n</aside>\n" +
            PocTemplate.StartMarker + "\n" + content + "\n" + PocTemplate.EndMarker + "\n" +
            "<div class=\"modal fade\" id=\"userModal\"></div>\n" +
            "<div class=\"modal fade\" id=\"imprintModal\"></div>\n" +
            "    " + PocTemplate.ScriptStartMarker + "\n    <script>\n    " + PocTemplate.ScriptPlaceholder + "\n    </script>\n    " + PocTemplate.ScriptEndMarker + "\n" +
            "</body></html>";
        return script.Length == 0 ? doc : PocTemplate.ReplaceScript(doc, script)!;
    }

    private static string Leaf(string label) =>
        $"<div class=\"nav-item\" title=\"{label}\"><span class=\"nav-label\">{label}</span></div>";

    private static string Group(string label, params string[] children) =>
        "<div class=\"nav-group open\">" +
        $"<div class=\"nav-item\" title=\"{label}\"><span class=\"nav-label\">{label}</span>" +
        "<svg class=\"ico nav-chevron\" viewBox=\"0 0 24 24\"><path d=\"M6 9l6 6 6-6\" /></svg></div>" +
        "<div class=\"nav-sub\">" + string.Concat(children.Select(Leaf)) + "</div></div>";

    private static string Section(string view, string inner = "x") =>
        $"<section class=\"page-view\" data-view=\"{view}\">{inner}</section>";

    private static string ActiveSection(string view, string inner = "x") =>
        $"<section class=\"page-view active\" data-view=\"{view}\">{inner}</section>";

    [Fact]
    public void Ok_WhenEveryLeafHasASection_CaseInsensitive()
    {
        var doc = Doc(Leaf("Dashboard") + Leaf("Orders"), Section("dashboard") + Section("Orders"));

        var report = PocAudit.Run(doc);

        Assert.StartsWith("POC audit: OK", report);
        Assert.Contains("2 menu leaves, 2 screens", report);
    }

    [Fact]
    public void ReportsMenuLeafWithoutSection()
    {
        var doc = Doc(Leaf("Dashboard") + Leaf("Reports"), Section("Dashboard"));

        var report = PocAudit.Run(doc);

        Assert.Contains("ISSUES", report);
        Assert.Contains("'Reports'", report);
    }

    [Fact]
    public void GroupHeaderIsNotALeaf_ButItsChildrenAre()
    {
        // "Manage" only expands; its children open screens. Only the missing CHILD is an issue.
        var doc = Doc(Group("Manage", "Users", "Teams"), Section("Users"));

        var report = PocAudit.Run(doc);

        Assert.DoesNotContain("'Manage'", report);
        Assert.Contains("'Teams'", report);
    }

    [Fact]
    public void WarnsOnSectionUnreachableFromMenu()
    {
        var doc = Doc(Leaf("Home"), Section("Home") + Section("Login"));

        var report = PocAudit.Run(doc);

        Assert.Contains("WARNINGS", report);
        Assert.Contains("data-view=\"Login\"", report);
        Assert.Contains("pocNavigate('Login')", report);
    }

    [Fact]
    public void ActiveLandingSectionOutsideMenu_IsNotWarnedUnreachable()
    {
        // The login/persona pattern: the landing screen carries "active" and has no menu item —
        // the shell keeps it visible on load, so it is reachable by definition.
        var doc = Doc(Leaf("Home"), ActiveSection("Login") + Section("Home"));

        var report = PocAudit.Run(doc);

        Assert.StartsWith("POC audit: OK", report);
        Assert.DoesNotContain("data-view=\"Login\"", report);
    }

    [Fact]
    public void ReportsHandlerCallingFunctionTheScriptNeverDefines()
    {
        // The shipped-POC bug this guards against: onclick="openUserModal()" with no such function
        // anywhere — the button throws "openUserModal is not defined" and feels dead.
        var content = Section("Home",
            "<button class=\"btn\" onclick=\"openUserModal()\">Add user</button>" +
            "<form onsubmit=\"saveUser(event)\"><button type=\"submit\">Save</button></form>");
        var doc = Doc(Leaf("Home"), content, script: "function renderUsers() {}\nrenderUsers();");

        var report = PocAudit.Run(doc);

        Assert.Contains("ISSUES", report);
        Assert.Contains("openUserModal", report);
        Assert.Contains("saveUser", report);
    }

    [Fact]
    public void HandlersCallingDefinedOrShellOrDottedFunctions_AreOk()
    {
        var content = Section("Home",
            "<button class=\"btn\" onclick=\"handleLogin(event)\">Login</button>" +          // function declaration
            "<button class=\"btn\" onclick=\"doIt()\">Go</button>" +                          // window.-assigned
            "<button class=\"btn\" onclick=\"refresh()\">Refresh</button>" +                  // const function expression
            "<button class=\"btn\" onclick=\"pocNavigate('Home')\" data-no-toast>Nav</button>" + // shell hook
            "<button class=\"btn\" onclick=\"this.form.reset()\">Reset</button>" +            // dotted: has a receiver
            "<button class=\"btn\" onclick=\"if (confirm('sure?')) doIt()\">Del</button>");   // keyword + builtin
        var doc = Doc(Leaf("Home"), content,
            script: "function handleLogin(e) {}\nwindow.doIt = function () {};\nconst refresh = function () {};");

        var report = PocAudit.Run(doc);

        Assert.StartsWith("POC audit: OK", report);
    }

    [Fact]
    public void ReportsReservedShellIdReuse_AndPlainDuplicates()
    {
        var content = Section("Home",
            "<div class=\"modal fade\" id=\"userModal\">reuses shell id</div>" +
            "<span id=\"twice\"></span><span id=\"twice\"></span>");

        var report = PocAudit.Run(Doc(Leaf("Home"), content));

        Assert.Contains("reserved by the shell", report);
        Assert.Contains("Duplicate id 'twice'", report);
    }

    [Fact]
    public void ReportsModalTriggerPointingAtMissingId()
    {
        var content = Section("Home", "<button class=\"btn\" data-bs-toggle=\"modal\" data-bs-target=\"#ghostModal\">Open</button>");

        var report = PocAudit.Run(Doc(Leaf("Home"), content));

        Assert.Contains("'#ghostModal'", report);
    }

    [Fact]
    public void ReportsCrudTableWithoutForm()
    {
        var content = Section("Home",
            "<table data-crud-table=\"order\"><thead><tr><th data-field=\"customer\">Customer</th><th data-actions></th></tr></thead><tbody></tbody></table>");

        var report = PocAudit.Run(Doc(Leaf("Home"), content));

        Assert.Contains("data-crud-table=\"order\"", report);
        Assert.Contains("data-crud-form", report);
    }

    [Fact]
    public void ReportsCrudFieldMismatch()
    {
        var content = Section("Home",
            "<table data-crud-table=\"order\"><thead><tr><th data-field=\"customer\">Customer</th><th data-field=\"status\">Status</th><th data-actions></th></tr></thead><tbody></tbody></table>" +
            "<div class=\"modal fade\" id=\"orderModal\"><form data-crud-form=\"order\"><input name=\"customer\"><button type=\"submit\">Save</button></form></div>");

        var report = PocAudit.Run(Doc(Leaf("Home"), content));

        Assert.Contains("[status]", report);
        Assert.DoesNotContain("[customer", report);
    }

    [Fact]
    public void WellWiredCrud_ReportsNothing()
    {
        var content = Section("Home",
            "<table data-crud-table=\"order\" data-crud-modal=\"#orderModal\"><thead><tr><th data-field=\"customer\">Customer</th><th data-actions></th></tr></thead><tbody></tbody></table>" +
            "<div class=\"modal fade\" id=\"orderModal\"><form data-crud-form=\"order\"><input name=\"customer\"><button type=\"submit\">Save</button></form></div>");

        var report = PocAudit.Run(Doc(Leaf("Home"), content));

        Assert.StartsWith("POC audit: OK", report);
        Assert.Contains("CRUD entities: order", report);
    }

    [Fact]
    public void InstructionalHtmlComments_DoNotProduceFalsePositives()
    {
        // The shell template ships a big instructional comment full of example markup; none of it is
        // real content, so it must not surface as entities, triggers or screens.
        var content =
            "<!-- example: <table data-crud-table=\"ENTITY\" data-crud-modal=\"#formModalId\"> and\n" +
            "     <section class=\"page-view\" data-view=\"LABEL\"> with data-bs-target=\"#someId\" -->\n" +
            Section("Home");

        var report = PocAudit.Run(Doc(Leaf("Home"), content));

        Assert.StartsWith("POC audit: OK", report);
        Assert.DoesNotContain("ENTITY", report);
        Assert.DoesNotContain("LABEL", report);
    }

    [Fact]
    public void StyleBlockMentioningScriptTag_DoesNotSwallowTheDocument()
    {
        // The shell's CSS carries a comment with the literal text "<script>"; a naive script-strip
        // starting there would swallow nav and sections and report an empty POC.
        var doc = Doc(Leaf("Home"), Section("Home"));
        doc = doc.Replace("<html><body>",
            "<html><head><style>/* the <script> toggles .active on nav clicks */</style></head><body>");

        var report = PocAudit.Run(doc);

        Assert.StartsWith("POC audit: OK", report);
        Assert.Contains("1 menu leaves, 1 screens", report);
    }

    [Fact]
    public void WarnsOnDuplicateFormsForOneEntity_WithoutFakeFieldMismatch()
    {
        // The classic slip: an empty wrapper <form> around the table plus the real form in the modal.
        // Field coverage is judged across both (no fake mismatch), the duplication itself is warned.
        var content = Section("Home",
            "<form data-crud-form=\"order\"><table data-crud-table=\"order\" data-crud-modal=\"#orderModal\"><thead><tr><th data-field=\"customer\">Customer</th><th data-actions></th></tr></thead><tbody></tbody></table></form>" +
            "<div class=\"modal fade\" id=\"orderModal\"><form data-crud-form=\"order\"><input name=\"customer\"><button type=\"submit\">Save</button></form></div>");

        var report = PocAudit.Run(Doc(Leaf("Home"), content));

        Assert.Contains("2 <form data-crud-form=\"order\">", report);
        Assert.DoesNotContain("ISSUES", report);
    }

    [Fact]
    public void WarnsWhenPocScriptStillEmpty()
    {
        var doc = Doc(Leaf("Home"), Section("Home"), script: "");

        var report = PocAudit.Run(doc);

        Assert.Contains("WARNINGS", report);
        Assert.Contains("SetPocScript", report);
        Assert.Contains("POC script: empty.", report);
    }

    [Fact]
    public void ReportsSeedPlaceholder_WhenSetPocContentNeverRan()
    {
        var doc = Doc(Leaf("Home"), PocTemplate.Placeholder);

        var report = PocAudit.Run(doc);

        Assert.Contains("SetPocContent", report);
    }

    [Fact]
    public void WarnsOnInlineScriptInsideContent()
    {
        var doc = Doc(Leaf("Home"), Section("Home", "<script>var sneaky = 1;</script>"));

        var report = PocAudit.Run(doc);

        Assert.Contains("inline <script>", report);
    }
}
