using ICOGenerator.Domain.Enums;
namespace ICOGenerator.Domain;
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ProjectStatus Status { get; set; } = ProjectStatus.Planning;
    public string? BackendGitUrl { get; set; }
    public string? FrontendGitUrl { get; set; }
    public string GenerationMode { get; set; } = "BoschTemplate";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<ProjectDocument> Documents { get; set; } = new List<ProjectDocument>();
    public ICollection<AgentConversation> Conversations { get; set; } = new List<AgentConversation>();
}
