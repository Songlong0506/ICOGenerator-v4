namespace ICOGenerator.Services.Requirements.Models;

public class BARequirementDocxResult
{
    public string AssistantMessage { get; set; } = "";
    public BrdDto Brd { get; set; } = new();
    public SrsDto Srs { get; set; } = new();
    public FsdDto Fsd { get; set; } = new();
    public UserStoriesDto UserStories { get; set; } = new();
    public AiDesignSpecDto AiDesignSpec { get; set; } = new();
}
