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
            new() { Label = "Orders", Children = new() { "All Orders", "Create Order" } },
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
        var items = new List<PocNavItem> { new() { Label = "R&D", Children = new() { "A<b>" } } };

        var updated = PocTemplate.ReplaceNav(Shell(), items);

        Assert.Contains("title=\"R&amp;D\"", updated);
        Assert.Contains("<span class=\"nav-label\">R&amp;D</span>", updated);
        Assert.Contains("<span class=\"nav-label\">A&lt;b&gt;</span>", updated);
    }

    [Fact]
    public void ReplaceNav_PicksMeaningfulIconsByLabel_WithDiacriticsAndDotFallback()
    {
        var items = new List<PocNavItem>
        {
            new() { Label = "Trang chủ" },                                  // home (Vietnamese diacritics)
            new() { Label = "Sản phẩm", Children = new() { "Giỏ hàng" } },  // package + cart child
            new() { Label = "Zzz" }                                          // unknown -> dot fallback
        };

        var updated = PocTemplate.ReplaceNav(Shell(), items);

        // Real, label-specific glyphs instead of the old generic square/circle.
        Assert.Contains("M3 9l9-7 9 7", updated);                                  // home roofline
        Assert.Contains("M21 16V8a2 2 0 0 0-1-1.73", updated);                     // package box
        Assert.Contains("<circle cx=\"9\" cy=\"21\" r=\"1\"/>", updated);          // cart wheel (child)
        Assert.Contains("fill=\"currentColor\"", updated);                         // solid-dot fallback

        // Every item still carries an .ico svg, and the old checkbox-style square is gone.
        Assert.Contains("<svg class=\"ico\" viewBox=\"0 0 24 24\">", updated);
        Assert.DoesNotContain("<rect x=\"3\" y=\"3\" width=\"18\" height=\"18\"", updated);
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
