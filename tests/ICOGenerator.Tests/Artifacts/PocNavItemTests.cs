using System.Text.Json;
using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

public class PocNavItemTests
{
    private static List<PocNavItem> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return PocNavItem.ParseList(doc.RootElement);
    }

    [Fact]
    public void ParseList_ReadsLeavesAndGroupsWithChildren()
    {
        var items = Parse("""[{"label":"Dashboard"},{"label":"Orders","children":["All Orders","Create Order"]}]""");

        Assert.Equal(2, items.Count);
        Assert.Equal("Dashboard", items[0].Label);
        Assert.Null(items[0].Children);
        Assert.Equal("Orders", items[1].Label);
        Assert.Equal(new[] { "All Orders", "Create Order" }, items[1].Children!.Select(c => c.Label));
    }

    [Fact]
    public void ParseList_ReadsIconOnItemsAndChildren()
    {
        var items = Parse("""[{"label":"Cart","icon":"cart3"},{"label":"Admin","children":[{"label":"Users","icon":"people"}]}]""");

        Assert.Equal("cart3", items[0].Icon);
        Assert.Null(items[1].Icon);
        Assert.Equal("people", items[1].Children!.Single().Icon);
    }

    [Fact]
    public void ParseList_AcceptsPlainStringsAsLeaves()
    {
        var items = Parse("""["Dashboard","Orders"]""");

        Assert.Equal(new[] { "Dashboard", "Orders" }, items.Select(x => x.Label));
        Assert.All(items, x => Assert.Null(x.Children));
    }

    [Fact]
    public void ParseList_IsCaseInsensitive_AndToleratesTitleNameAliases()
    {
        var items = Parse("""[{"Label":"X","CHILDREN":["y"]},{"title":"T"},{"name":"N"}]""");

        Assert.Equal(new[] { "X", "T", "N" }, items.Select(x => x.Label));
        Assert.Equal(new[] { "y" }, items[0].Children!.Select(c => c.Label));
    }

    [Fact]
    public void ParseList_AcceptsChildrenAsObjects()
    {
        var items = Parse("""[{"label":"Group","children":[{"label":"c1"},{"title":"c2"}]}]""");

        Assert.Single(items);
        Assert.Equal(new[] { "c1", "c2" }, items[0].Children!.Select(c => c.Label));
    }

    [Fact]
    public void ParseList_SkipsBlankLabelsAndChildren_AndTrims()
    {
        var items = Parse("""[{"label":"  "},{"label":" Real ","children":["","  "," Kid "]}]""");

        Assert.Single(items);
        Assert.Equal("Real", items[0].Label);
        Assert.Equal(new[] { "Kid" }, items[0].Children!.Select(c => c.Label));
    }

    [Fact]
    public void ParseList_LeavesChildrenNull_WhenChildrenArrayEmpty()
    {
        var items = Parse("""[{"label":"X","children":[]}]""");

        Assert.Single(items);
        Assert.Null(items[0].Children);
    }

    [Theory]
    [InlineData("{\"label\":\"x\"}")]
    [InlineData("\"just a string\"")]
    [InlineData("123")]
    [InlineData("[]")]
    public void ParseList_ReturnsEmpty_ForNonArraysOrEmptyArray(string json)
    {
        Assert.Empty(Parse(json));
    }

    [Fact]
    public void ParseList_UnwrapsJsonEncodedArrayString()
    {
        // A model may double-encode navItems (the array arrives as a JSON string). Dropping it left
        // POCs with page-view sections but no sidebar tab to open them.
        var items = Parse("""
            "[{\"label\":\"Dashboard\",\"icon\":\"house\"},{\"label\":\"Orders\",\"children\":[\"All Orders\"]}]"
            """);

        Assert.Equal(new[] { "Dashboard", "Orders" }, items.Select(x => x.Label));
        Assert.Equal("house", items[0].Icon);
        Assert.Equal(new[] { "All Orders" }, items[1].Children!.Select(c => c.Label));
    }

    [Theory]
    [InlineData("\"[not json\"")]                 // starts like an array but is not valid JSON
    [InlineData("\"  {\\\"label\\\":\\\"x\\\"}\"")] // encoded JSON, but not an array
    public void ParseList_ReturnsEmpty_ForUnparsableEncodedStrings(string json)
    {
        Assert.Empty(Parse(json));
    }
}
