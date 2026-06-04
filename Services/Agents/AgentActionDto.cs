using System.Text.Json;

namespace ICOGenerator.Services.Agents;

public class AgentActionDto
{
    public string Type { get; set; } = string.Empty;
    public string? Tool { get; set; }
    public Dictionary<string, JsonElement> Args { get; set; } = [];
    public string? Content { get; set; }
}
