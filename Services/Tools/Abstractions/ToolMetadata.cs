namespace ICOGenerator.Services.Tools.Abstractions;

public record ToolMetadata(string Name, string Description, string InputSchema, string RiskLevel = "Low", TimeSpan? Timeout = null, int MaxOutputCharacters = 12000);
