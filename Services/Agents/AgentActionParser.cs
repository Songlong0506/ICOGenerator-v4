using System.Text.Json;
using ICOGenerator.Services.Models;

namespace ICOGenerator.Services.Agents;

public class AgentActionParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool TryParse(string response, out AgentActionDto? action)
    {
        action = null;

        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            var json = JsonExtractor.Extract(response);
            action = JsonSerializer.Deserialize<AgentActionDto>(json, JsonOptions);
            return action != null && !string.IsNullOrWhiteSpace(action.Type);
        }
        catch
        {
            return false;
        }
    }
}
