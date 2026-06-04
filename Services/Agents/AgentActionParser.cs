using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Agents;

public class AgentActionParser
{
    public bool TryParse(string response, out AgentActionDto? action)
    {
        action = null;

        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            action = JsonExtractor.ExtractAndDeserialize<AgentActionDto>(response);
            return action != null && !string.IsNullOrWhiteSpace(action.Type);
        }
        catch
        {
            return false;
        }
    }
}
