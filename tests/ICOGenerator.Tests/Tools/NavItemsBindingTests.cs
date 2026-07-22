using System.Text.Json;
using ICOGenerator.Services.Artifacts;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Tools;

/// <summary>
/// Regression guard for the "POC has sections but no sidebar tabs" bug: exercises the REAL agent
/// binding path (AIFunctionFactory, same as AgentRunService) into a method with SetPocContent's
/// signature, then feeds the received 'navItems' element to PocNavItem.ParseList — both when the
/// model sends a proper JSON array and when it double-encodes the array as a string (the slip that
/// used to silently drop the whole menu).
/// </summary>
public class NavItemsBindingTests
{
    private JsonElement? _receivedNavItems;
    private bool _called;

    // Same signature as WorkspaceTools.SetPocContent so the framework builds the same schema/binding.
    public Task<string> SetPocContent(string content, string? appName = null, string? breadcrumb = null, JsonElement? navItems = null)
    {
        _called = true;
        _receivedNavItems = navItems;
        return Task.FromResult("ok");
    }

    private async Task<(JsonValueKind Kind, int Parsed)> RoundTrip(string argsJson)
    {
        _called = false;
        _receivedNavItems = null;
        var fn = AIFunctionFactory.Create(GetType().GetMethod(nameof(SetPocContent))!, this);
        var args = new AIFunctionArguments();
        foreach (var p in JsonDocument.Parse(argsJson).RootElement.EnumerateObject())
            args[p.Name] = p.Value.Clone();
        await fn.InvokeAsync(args);
        Assert.True(_called);
        if (_receivedNavItems is not { } el) return (JsonValueKind.Undefined, -1);
        return (el.ValueKind, PocNavItem.ParseList(el).Count);
    }

    [Fact]
    public async Task NavItems_SentAsRealArray_ParsesAllItems()
    {
        var (kind, parsed) = await RoundTrip(
            """{"content":"<section class=\"page-view active\" data-view=\"A\">x</section>","appName":"App","breadcrumb":"Home","navItems":[{"label":"A","icon":"house"},{"label":"B"}]}""");
        Assert.Equal(JsonValueKind.Array, kind);
        Assert.Equal(2, parsed);
    }

    [Fact]
    public async Task NavItems_SentAsJsonEncodedString_StillParsesAllItems()
    {
        var (kind, parsed) = await RoundTrip(
            """{"content":"<section class=\"page-view active\" data-view=\"A\">x</section>","navItems":"[{\"label\":\"A\"},{\"label\":\"B\"}]"}""");
        Assert.Equal(JsonValueKind.String, kind);
        Assert.Equal(2, parsed);
    }
}
