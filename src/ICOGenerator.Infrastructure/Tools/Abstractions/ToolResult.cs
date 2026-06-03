namespace ICOGenerator.Services.Tools.Abstractions;

public record ToolResult<TOutput>(bool IsSuccess, TOutput? Output, string? Error = null)
{
    public string ToObservation() => IsSuccess ? Output?.ToString() ?? string.Empty : $"Tool failed: {Error}";
}
