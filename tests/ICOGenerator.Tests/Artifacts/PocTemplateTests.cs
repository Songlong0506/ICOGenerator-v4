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
    public void ReplaceAppName_SwapsTextBetweenMarkers_AndKeepsMarkers()
    {
        var shell = $"<span class=\"app-name\">{PocTemplate.AppNameStartMarker}App Name{PocTemplate.AppNameEndMarker}</span>";

        var updated = PocTemplate.ReplaceAppName(shell, "Fleet Manager");

        Assert.NotNull(updated);
        Assert.Contains("Fleet Manager", updated);
        Assert.DoesNotContain(">App Name<", updated);
        Assert.Contains(PocTemplate.AppNameStartMarker, updated);
        Assert.Contains(PocTemplate.AppNameEndMarker, updated);
    }

    [Fact]
    public void ReplaceNav_SwapsNavRegion_AndKeepsMarkers()
    {
        var shell = $"<nav>{PocTemplate.NavStartMarker}\n<div>Module A</div>\n{PocTemplate.NavEndMarker}</nav>";

        var updated = PocTemplate.ReplaceNav(shell, "<div class=\"nav-item\">Vehicles</div>");

        Assert.NotNull(updated);
        Assert.Contains("Vehicles", updated);
        Assert.DoesNotContain("Module A", updated);
        Assert.Contains(PocTemplate.NavStartMarker, updated);
        Assert.Contains(PocTemplate.NavEndMarker, updated);
    }

    [Fact]
    public void ReplaceBreadcrumb_SwapsBreadcrumb_AndKeepsMarkers()
    {
        var shell = $"<div class=\"breadcrumb\">{PocTemplate.BreadcrumbStartMarker}Home<!-- x -->{PocTemplate.BreadcrumbEndMarker}</div>";

        var updated = PocTemplate.ReplaceBreadcrumb(shell, "Fleet &rsaquo; Vehicles");

        Assert.NotNull(updated);
        Assert.Contains("Fleet &rsaquo; Vehicles", updated);
        Assert.Contains(PocTemplate.BreadcrumbStartMarker, updated);
        Assert.Contains(PocTemplate.BreadcrumbEndMarker, updated);
    }

    [Fact]
    public void ShellReplacers_ReturnNull_WhenMarkersMissing()
    {
        Assert.Null(PocTemplate.ReplaceAppName("<span>App Name</span>", "x"));
        Assert.Null(PocTemplate.ReplaceNav("<nav></nav>", "x"));
        Assert.Null(PocTemplate.ReplaceBreadcrumb("<div></div>", "x"));
    }

    [Fact]
    public void RealTemplate_ExposesAllShellMarkers()
    {
        var templatePath = Path.Combine(FindRepoRoot(), "Prompts", "Design", "poc-template.html");
        var template = File.ReadAllText(templatePath);

        Assert.Contains(PocTemplate.AppNameStartMarker, template);
        Assert.Contains(PocTemplate.AppNameEndMarker, template);
        Assert.Contains(PocTemplate.NavStartMarker, template);
        Assert.Contains(PocTemplate.NavEndMarker, template);
        Assert.Contains(PocTemplate.BreadcrumbStartMarker, template);
        Assert.Contains(PocTemplate.BreadcrumbEndMarker, template);

        // The shell regions must remain editable after the content region is seeded.
        var seeded = PocTemplate.SeedFromTemplate(template);
        Assert.NotNull(seeded);
        Assert.NotNull(PocTemplate.ReplaceAppName(seeded!, "Fleet Manager"));
        Assert.NotNull(PocTemplate.ReplaceNav(seeded!, "<div class=\"nav-item\">Vehicles</div>"));
        Assert.NotNull(PocTemplate.ReplaceBreadcrumb(seeded!, "Home"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ICOGenerator.csproj")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
