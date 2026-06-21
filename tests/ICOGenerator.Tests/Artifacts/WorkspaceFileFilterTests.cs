using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

public class WorkspaceFileFilterTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "icogen-ws");

    [Fact]
    public void IsInRegenerableDirectory_ReturnsFalse_ForFilesUnderRealSourceDirs()
    {
        Assert.False(IsRegenerable("README.md"));
        Assert.False(IsRegenerable(Path.Combine("src", "index.js")));
        Assert.False(IsRegenerable(Path.Combine("04_Implementation", "src", "frontend", "app.component.ts")));
    }

    [Theory]
    [InlineData("node_modules")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".git")]
    [InlineData(".vs")]
    public void IsInRegenerableDirectory_ReturnsTrue_ForEachRegenerableDirectory(string dir)
    {
        Assert.True(IsRegenerable(Path.Combine(dir, "anything.js")));
    }

    [Fact]
    public void IsInRegenerableDirectory_ReturnsTrue_ForNestedRegenerableDirectory()
    {
        // The real workspace shape that motivated the filter: a built Angular project's deps.
        Assert.True(IsRegenerable(Path.Combine("04_Implementation", "src", "frontend", "node_modules", "@angular", "core.js")));
    }

    [Fact]
    public void IsInRegenerableDirectory_IsCaseInsensitive()
    {
        Assert.True(IsRegenerable(Path.Combine("Node_Modules", "left-pad", "index.js")));
        Assert.True(IsRegenerable(Path.Combine("BIN", "app.dll")));
    }

    [Fact]
    public void IsInRegenerableDirectory_ReturnsFalse_WhenRegenerableNameIsTheFileItself()
    {
        // Only directory segments count; a file literally named "bin" or "obj" is a real artifact.
        Assert.False(IsRegenerable("bin"));
        Assert.False(IsRegenerable(Path.Combine("src", "obj")));
    }

    private static bool IsRegenerable(string relativePath) =>
        WorkspaceFileFilter.IsInRegenerableDirectory(Root, Path.Combine(Root, relativePath));
}
