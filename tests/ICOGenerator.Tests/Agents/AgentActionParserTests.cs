using ICOGenerator.Services.Agents;
using Xunit;

namespace ICOGenerator.Tests.Agents;

public class AgentActionParserTests
{
    [Fact]
    public void TryParse_ReturnsFinalAction_WhenResponseContainsJsonObject()
    {
        var parser = new AgentActionParser();

        var parsed = parser.TryParse("""
            The model responded with:
            {
              "type": "final",
              "content": "Done"
            }
            """, out var action);

        Assert.True(parsed);
        Assert.NotNull(action);
        Assert.Equal("final", action!.Type);
        Assert.Equal("Done", action.Content);
    }

    [Fact]
    public void TryParse_ReturnsToolAction_WhenResponseIsMarkdownFencedJson()
    {
        var parser = new AgentActionParser();

        var parsed = parser.TryParse("""
            ```json
            {
              "type": "tool",
              "tool": "ReadFile",
              "args": {
                "relativePath": "Program.cs"
              }
            }
            ```
            """, out var action);

        Assert.True(parsed);
        Assert.NotNull(action);
        Assert.Equal("tool", action!.Type);
        Assert.Equal("ReadFile", action.Tool);
        Assert.True(action.Args.ContainsKey("relativePath"));
    }

    [Fact]
    public void TryParse_ReturnsFalse_WhenResponseIsNotJson()
    {
        var parser = new AgentActionParser();

        var parsed = parser.TryParse("I cannot produce a structured action.", out var action);

        Assert.False(parsed);
        Assert.Null(action);
    }
}
