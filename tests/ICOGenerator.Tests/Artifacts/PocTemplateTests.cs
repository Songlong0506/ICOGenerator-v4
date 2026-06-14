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
}
