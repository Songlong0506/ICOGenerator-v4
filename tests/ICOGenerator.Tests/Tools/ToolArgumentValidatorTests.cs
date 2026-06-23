using System.Reflection;
using System.Text.Json;
using ICOGenerator.Services.Tools.Registry;
using Xunit;

namespace ICOGenerator.Tests.Tools;

public class ToolArgumentValidatorTests
{
    // Mirrors a real tool signature (like SetPocContent): one required parameter plus optional ones.
    private static void Sample(string content, string? appName = null, int count = 0) { }

    // A required parameter plus a CancellationToken, which the model never supplies and must be ignored.
    private static void SampleWithToken(string path, CancellationToken token) { }

    private static MethodInfo Method(string name) =>
        typeof(ToolArgumentValidatorTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Flags_RequiredArgument_WhenAbsent()
    {
        var missing = ToolArgumentValidator.FindMissingRequiredArguments(Method(nameof(Sample)), Args("{}"));

        Assert.Equal(new[] { "content" }, missing);
    }

    [Fact]
    public void Flags_RequiredArgument_WhenPresentButJsonNull()
    {
        var missing = ToolArgumentValidator.FindMissingRequiredArguments(Method(nameof(Sample)), Args("""{"content":null}"""));

        Assert.Equal(new[] { "content" }, missing);
    }

    [Fact]
    public void Passes_WhenRequiredArgumentPresent_AndOptionalsAbsent()
    {
        var missing = ToolArgumentValidator.FindMissingRequiredArguments(
            Method(nameof(Sample)), Args("""{"content":"<section/>"}"""));

        Assert.Empty(missing);
    }

    [Fact]
    public void Passes_WhenRequiredArgumentIsExplicitEmptyString()
    {
        // An explicit "" is "present" — a deliberate empty value, distinct from a dropped/truncated arg.
        var missing = ToolArgumentValidator.FindMissingRequiredArguments(
            Method(nameof(Sample)), Args("""{"content":""}"""));

        Assert.Empty(missing);
    }

    [Fact]
    public void Ignores_CancellationTokenParameter()
    {
        var missing = ToolArgumentValidator.FindMissingRequiredArguments(
            Method(nameof(SampleWithToken)), Args("""{"path":"a.txt"}"""));

        Assert.Empty(missing);
    }
}
