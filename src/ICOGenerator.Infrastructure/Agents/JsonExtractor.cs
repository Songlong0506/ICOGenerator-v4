namespace ICOGenerator.Services.Agents;
public static class JsonExtractor
{
    public static string Extract(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
                text = text.Substring(firstNewLine + 1, lastFence - firstNewLine - 1).Trim();
        }
        var start = text.IndexOf('{');
        if (start < 0) return text;
        int depth = 0; bool inString = false; bool escape = false;
        for (int i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            if (c == '}') depth--;
            if (depth == 0) return text.Substring(start, i - start + 1);
        }
        return text;
    }
}
