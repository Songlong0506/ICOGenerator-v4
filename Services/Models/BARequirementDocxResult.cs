namespace ICOGenerator.Services.Models
{
    public class BARequirementDocxResult
    {
        public string AssistantMessage { get; set; } = "";
        public BrdDto Brd { get; set; } = new();
        public SrsDto Srs { get; set; } = new();
        public UserStoriesDto UserStories { get; set; } = new();
    }
}
