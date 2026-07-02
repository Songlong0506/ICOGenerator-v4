using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

public class PocTemplateTests
{
    private static string TemplateWith(string region) =>
        $"<html><body>\n{PocTemplate.StartMarker}\n{region}\n{PocTemplate.EndMarker}\n</body></html>";

    [Fact]
    public void SeedFromTemplate_CollapsesRegionToPlaceholder_AndKeepsMarkers()
    {
        var template = TemplateWith("<p>old demo content</p>");

        var seeded = PocTemplate.SeedFromTemplate(template);

        Assert.NotNull(seeded);
        Assert.Contains(PocTemplate.StartMarker, seeded);
        Assert.Contains(PocTemplate.EndMarker, seeded);
        Assert.Contains(PocTemplate.Placeholder, seeded);
        Assert.DoesNotContain("old demo content", seeded);
    }

    [Fact]
    public void SeedFromTemplate_ReturnsNull_WhenMarkersMissing()
    {
        Assert.Null(PocTemplate.SeedFromTemplate("<html><body>no markers here</body></html>"));
    }

    [Fact]
    public void ReplaceContent_SwapsRegion_AndKeepsMarkersAndShell()
    {
        var seeded = PocTemplate.SeedFromTemplate(TemplateWith("anything"))!;

        var updated = PocTemplate.ReplaceContent(seeded, "<section>feature UI</section>");

        Assert.NotNull(updated);
        Assert.Contains("<section>feature UI</section>", updated);
        Assert.DoesNotContain(PocTemplate.Placeholder, updated);
        Assert.Contains(PocTemplate.StartMarker, updated);
        Assert.Contains(PocTemplate.EndMarker, updated);
        Assert.StartsWith("<html><body>", updated);
        Assert.EndsWith("</body></html>", updated.TrimEnd('\n'));
    }

    [Fact]
    public void ReplaceContent_ReturnsNull_WhenMarkersMissing()
    {
        Assert.Null(PocTemplate.ReplaceContent("<html>nothing</html>", "x"));
    }

    [Fact]
    public void AppendContent_OnSeededFile_DropsPlaceholder_AddsContent_KeepsMarkers()
    {
        var seeded = PocTemplate.SeedFromTemplate(TemplateWith("anything"))!;

        var updated = PocTemplate.AppendContent(seeded, "<section>first</section>");

        Assert.NotNull(updated);
        Assert.Contains("<section>first</section>", updated);
        Assert.DoesNotContain(PocTemplate.Placeholder, updated);
        Assert.Contains(PocTemplate.StartMarker, updated);
        Assert.Contains(PocTemplate.EndMarker, updated);
    }

    [Fact]
    public void AppendContent_KeepsEarlierChunks_AndPreservesOrder()
    {
        var doc = PocTemplate.SeedFromTemplate(TemplateWith("anything"))!;

        doc = PocTemplate.AppendContent(doc, "<section>one</section>")!;
        doc = PocTemplate.AppendContent(doc, "<section>two</section>")!;
        doc = PocTemplate.AppendContent(doc, "<div class=\"modal\">three</div>")!;

        Assert.Contains("<section>one</section>", doc);
        Assert.Contains("<section>two</section>", doc);
        Assert.Contains("<div class=\"modal\">three</div>", doc);
        // Appended in order, all inside the (still intact) content region.
        Assert.True(doc.IndexOf("one", System.StringComparison.Ordinal) < doc.IndexOf("two", System.StringComparison.Ordinal));
        Assert.True(doc.IndexOf("two", System.StringComparison.Ordinal) < doc.IndexOf("three", System.StringComparison.Ordinal));
        Assert.True(doc.IndexOf("three", System.StringComparison.Ordinal) < doc.IndexOf(PocTemplate.EndMarker, System.StringComparison.Ordinal));
    }

    [Fact]
    public void AppendContent_AfterSetPocContent_AddsToExistingFirstScreen()
    {
        // SetPocContent writes the first screen via ReplaceContent; AppendPocContent adds the rest.
        var doc = PocTemplate.ReplaceContent(PocTemplate.SeedFromTemplate(TemplateWith("x"))!, "<section>screen-1</section>")!;

        doc = PocTemplate.AppendContent(doc, "<section>screen-2</section>")!;

        Assert.Contains("<section>screen-1</section>", doc);
        Assert.Contains("<section>screen-2</section>", doc);
        Assert.True(doc.IndexOf("screen-1", System.StringComparison.Ordinal) < doc.IndexOf("screen-2", System.StringComparison.Ordinal));
    }

    [Fact]
    public void AppendContent_ReturnsNull_WhenMarkersMissing()
    {
        Assert.Null(PocTemplate.AppendContent("<html>nothing</html>", "x"));
    }

    [Fact]
    public void AppendContent_NoOp_WhenAdditionBlank()
    {
        var seeded = PocTemplate.SeedFromTemplate(TemplateWith("anything"))!;

        Assert.Equal(seeded, PocTemplate.AppendContent(seeded, "   "));
    }

    // A trimmed shell that keeps the exact anchor markup SeedFromTemplate copies from
    // poc-template.html, so these tests fail if the template's title/app-name/breadcrumb/nav
    // markup ever drifts away from what PocTemplate looks for.
    private const string ShellHead =
"""
<html>
<head><title>POC</title></head>
<body>
    <aside class="sidebar">
        <div class="sidebar-head">
            <span class="app-name">App Name</span>
        </div>
        <nav class="sidebar-nav">
            <div class="nav-item active" title="Overview"><span class="nav-label">Overview</span></div>
            <div class="nav-group open"><div class="nav-item" title="Module A"><span class="nav-label">Module A</span></div></div>
        </nav>
    </aside>
    <header class="topbar"><div class="breadcrumb">Home <span class="sep">&rsaquo;</span> Page Title</div></header>

""";

    private static string Shell() =>
        ShellHead + PocTemplate.StartMarker + "\n<!-- POC_CONTENT -->\n" + PocTemplate.EndMarker + "\n</body>\n</html>";

    [Fact]
    public void ReplaceAppName_SetsSidebarHeaderAndBrowserTitle()
    {
        var updated = PocTemplate.ReplaceAppName(Shell(), "Order Management");

        Assert.Contains("<span class=\"app-name\">Order Management</span>", updated);
        Assert.Contains("<title>Order Management</title>", updated);
        Assert.DoesNotContain(">App Name<", updated);
        Assert.DoesNotContain("<title>POC</title>", updated);
    }

    [Fact]
    public void ReplaceAppName_EncodesHtml()
    {
        var updated = PocTemplate.ReplaceAppName(Shell(), "Tom & Jerry <Co>");

        Assert.Contains("<span class=\"app-name\">Tom &amp; Jerry &lt;Co&gt;</span>", updated);
    }

    [Fact]
    public void ReplaceAppName_NoOp_WhenBlankOrAnchorMissing()
    {
        var shell = Shell();
        Assert.Equal(shell, PocTemplate.ReplaceAppName(shell, "   "));
        Assert.Equal("<html>no anchors</html>", PocTemplate.ReplaceAppName("<html>no anchors</html>", "X"));
    }

    [Fact]
    public void ReplaceBreadcrumb_SetsText_AndEncodes()
    {
        var updated = PocTemplate.ReplaceBreadcrumb(Shell(), "Home > Orders");

        Assert.Contains("<div class=\"breadcrumb\">Home &gt; Orders</div>", updated);
        Assert.DoesNotContain("Page Title", updated);
    }

    [Fact]
    public void ReplaceNav_RendersLeafAndGroup_FirstActive_FirstGroupOpen()
    {
        var items = new List<PocNavItem>
        {
            new() { Label = "Dashboard" },
            new() { Label = "Orders", Children = new() { new() { Label = "All Orders" }, new() { Label = "Create Order" } } },
            new() { Label = "Settings" }
        };

        var updated = PocTemplate.ReplaceNav(Shell(), items);

        // Template placeholder menu is gone.
        Assert.DoesNotContain("Module A", updated);
        Assert.DoesNotContain(">Overview<", updated);

        // First entry is the active leaf; the group is expanded with its sub-items.
        Assert.Contains("<div class=\"nav-item active\" title=\"Dashboard\">", updated);
        Assert.Contains("<div class=\"nav-group open\">", updated);
        Assert.Contains("title=\"Orders\"", updated);
        Assert.Contains("<span class=\"nav-label\">All Orders</span>", updated);
        Assert.Contains("<span class=\"nav-label\">Create Order</span>", updated);
        Assert.Contains("<div class=\"nav-item\" title=\"Settings\">", updated);

        // Exactly one active item and one open group.
        Assert.Equal(1, Count(updated, "nav-item active"));
        Assert.Equal(1, Count(updated, "nav-group open"));

        // Shell around the nav is preserved.
        Assert.Contains("<aside class=\"sidebar\">", updated);
        Assert.Contains("</nav>", updated);
    }

    [Fact]
    public void ReplaceNav_EncodesLabels()
    {
        var items = new List<PocNavItem> { new() { Label = "R&D", Children = new() { new() { Label = "A<b>" } } } };

        var updated = PocTemplate.ReplaceNav(Shell(), items);

        Assert.Contains("title=\"R&amp;D\"", updated);
        Assert.Contains("<span class=\"nav-label\">R&amp;D</span>", updated);
        Assert.Contains("<span class=\"nav-label\">A&lt;b&gt;</span>", updated);
    }

    [Fact]
    public void ReplaceNav_UsesAgentIconOnItemsAndChildren_ElseDefault()
    {
        var items = new List<PocNavItem>
        {
            new() { Label = "Products", Icon = "box-seam", Children = new() { new() { Label = "Cart", Icon = "cart3" } } },
            new() { Label = "Reports", Icon = "graph-up-arrow" },
            new() { Label = "Untagged" } // no icon -> default
        };

        var updated = PocTemplate.ReplaceNav(Shell(), items);

        Assert.Contains("<i class=\"bi bi-box-seam\" aria-hidden=\"true\"></i>", updated);        // explicit (group header)
        Assert.Contains("<i class=\"bi bi-cart3\" aria-hidden=\"true\"></i>", updated);           // explicit (child)
        Assert.Contains("<i class=\"bi bi-graph-up-arrow\" aria-hidden=\"true\"></i>", updated);  // explicit (leaf)
        Assert.Contains("<i class=\"bi bi-dot\" aria-hidden=\"true\"></i>", updated);             // default fallback

        // The old inline placeholder square/circle for nav icons is gone.
        Assert.DoesNotContain("<svg class=\"ico\" viewBox=\"0 0 24 24\"><rect", updated);
        Assert.DoesNotContain("<svg class=\"ico\" viewBox=\"0 0 24 24\"><circle", updated);
    }

    [Fact]
    public void ReplaceNav_SanitizesAgentSuppliedIconName()
    {
        // Strips a leading "bi-" and rejects anything outside [a-z0-9-] so it can't break the class attribute.
        var items = new List<PocNavItem> { new() { Label = "X", Icon = "bi-Cart3\" onload=\"x" } };

        var updated = PocTemplate.ReplaceNav(Shell(), items);

        Assert.Contains("<i class=\"bi bi-cart3onloadx\" aria-hidden=\"true\"></i>", updated);
        Assert.DoesNotContain("onload=\"x", updated);
    }

    [Fact]
    public void ReplaceNav_NoOp_WhenNothingRenderableOrNavMissing()
    {
        var shell = Shell();
        Assert.Equal(shell, PocTemplate.ReplaceNav(shell, new List<PocNavItem>()));
        Assert.Equal(shell, PocTemplate.ReplaceNav(shell, new List<PocNavItem> { new() { Label = "   " } }));
        Assert.Equal("<html>no nav</html>",
            PocTemplate.ReplaceNav("<html>no nav</html>", new List<PocNavItem> { new() { Label = "X" } }));
    }

    [Fact]
    public void ShellCustomization_ComposesWithContentReplacement()
    {
        var doc = PocTemplate.ReplaceContent(Shell(), "<section>feature</section>")!;
        doc = PocTemplate.ReplaceAppName(doc, "Fleet Portal");
        doc = PocTemplate.ReplaceBreadcrumb(doc, "Home > Fleet");
        doc = PocTemplate.ReplaceNav(doc, new List<PocNavItem> { new() { Label = "Vehicles" } });

        Assert.Contains("<section>feature</section>", doc);
        Assert.Contains("<span class=\"app-name\">Fleet Portal</span>", doc);
        Assert.Contains("<title>Fleet Portal</title>", doc);
        Assert.Contains("title=\"Vehicles\"", doc);
        Assert.DoesNotContain("App Name", doc);
        Assert.DoesNotContain("Module A", doc);
        Assert.Contains(PocTemplate.StartMarker, doc);
        Assert.Contains(PocTemplate.EndMarker, doc);
    }

    // ---- POC_SCRIPT region (page logic written via SetPocScript/AppendPocScript) ----

    private static string TemplateWithScriptRegion(string content = "<p>c</p>") =>
        $"<html><body>\n{PocTemplate.StartMarker}\n{content}\n{PocTemplate.EndMarker}\n" +
        $"    {PocTemplate.ScriptStartMarker}\n    <script>\n    {PocTemplate.ScriptPlaceholder}\n    </script>\n    {PocTemplate.ScriptEndMarker}\n</body></html>";

    [Fact]
    public void ReplaceScript_WritesJsIntoRegion_DropsPlaceholder_KeepsMarkers()
    {
        var updated = PocTemplate.ReplaceScript(TemplateWithScriptRegion(), "function login(){ pocNavigate('Dashboard'); }");

        Assert.NotNull(updated);
        Assert.Contains("function login(){ pocNavigate('Dashboard'); }", updated);
        Assert.DoesNotContain(PocTemplate.ScriptPlaceholder, updated);
        Assert.Contains(PocTemplate.ScriptStartMarker, updated);
        Assert.Contains(PocTemplate.ScriptEndMarker, updated);
        Assert.Equal(1, Count(updated!, "<script>"));
    }

    [Fact]
    public void ReplaceScript_SecondCallReplaces_InsteadOfStacking()
    {
        var doc = PocTemplate.ReplaceScript(TemplateWithScriptRegion(), "var first = 1;")!;

        var again = PocTemplate.ReplaceScript(doc, "var second = 2;")!;

        Assert.Contains("var second = 2;", again);
        Assert.DoesNotContain("var first = 1;", again);
        Assert.Equal(1, Count(again, "<script>"));
    }

    [Fact]
    public void ReplaceScript_StripsScriptTagWrapper_AndMarkdownFence()
    {
        // Models sometimes wrap the JS despite instructions; the wrapper is stripped, not doubled.
        var wrapped = PocTemplate.ReplaceScript(TemplateWithScriptRegion(), "<script>\nvar x = 1;\n</script>")!;
        Assert.Contains("var x = 1;", wrapped);
        Assert.Equal(1, Count(wrapped, "<script"));

        var fenced = PocTemplate.ReplaceScript(TemplateWithScriptRegion(), "```js\nvar y = 2;\n```")!;
        Assert.Contains("var y = 2;", fenced);
        Assert.DoesNotContain("```", fenced);
    }

    [Fact]
    public void ReplaceScript_EscapesEarlyScriptTermination()
    {
        // "</script" inside the JS would end the inline element early; it is escaped the standard way.
        var updated = PocTemplate.ReplaceScript(TemplateWithScriptRegion(), "var s = \"</script>\";")!;

        Assert.Contains("var s = \"<\\/script>\";", updated);
    }

    [Fact]
    public void ReplaceScript_GraftsRegionBeforeBody_WhenRegionMissing()
    {
        // Workspace seeded from an older template (no script region): the region is grafted in
        // before </body> so SetPocScript keeps working, and later calls replace instead of stacking.
        var legacy = TemplateWith("<p>old</p>");

        var updated = PocTemplate.ReplaceScript(legacy, "var z = 3;");

        Assert.NotNull(updated);
        Assert.Contains(PocTemplate.ScriptStartMarker, updated);
        Assert.Contains("var z = 3;", updated);
        Assert.True(updated!.IndexOf("var z = 3;", System.StringComparison.Ordinal)
                    < updated.IndexOf("</body>", System.StringComparison.Ordinal));

        var again = PocTemplate.ReplaceScript(updated, "var w = 4;")!;
        Assert.Contains("var w = 4;", again);
        Assert.DoesNotContain("var z = 3;", again);
    }

    [Fact]
    public void ReplaceScript_ReturnsNull_WhenNoRegionAndNoBody()
    {
        Assert.Null(PocTemplate.ReplaceScript("<html>nothing</html>", "var x = 1;"));
    }

    [Fact]
    public void ReplaceScript_NoOp_WhenBlank()
    {
        var doc = TemplateWithScriptRegion();
        Assert.Equal(doc, PocTemplate.ReplaceScript(doc, "   "));
    }

    [Fact]
    public void AppendScript_AddsAfterExistingChunk_InOneScriptElement()
    {
        var doc = PocTemplate.ReplaceScript(TemplateWithScriptRegion(), "function a(){}")!;

        doc = PocTemplate.AppendScript(doc, "function b(){}")!;

        Assert.Contains("function a(){}", doc);
        Assert.Contains("function b(){}", doc);
        Assert.True(doc.IndexOf("function a(){}", System.StringComparison.Ordinal)
                    < doc.IndexOf("function b(){}", System.StringComparison.Ordinal));
        Assert.Equal(1, Count(doc, "<script>"));
    }

    [Fact]
    public void AppendScript_OnEmptyRegion_ActsLikeSet()
    {
        var doc = PocTemplate.AppendScript(TemplateWithScriptRegion(), "function only(){}");

        Assert.NotNull(doc);
        Assert.Contains("function only(){}", doc);
        Assert.DoesNotContain(PocTemplate.ScriptPlaceholder, doc);
    }

    [Fact]
    public void GetScriptBody_EmptyOnSeed_ReturnsJsAfterSet()
    {
        Assert.Equal(string.Empty, PocTemplate.GetScriptBody(TemplateWithScriptRegion()));

        var doc = PocTemplate.ReplaceScript(TemplateWithScriptRegion(), "var q = 1;")!;
        Assert.Equal("var q = 1;", PocTemplate.GetScriptBody(doc));
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
