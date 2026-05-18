using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AiModel> AiModels => Set<AiModel>();
    public DbSet<ToolDefinition> ToolDefinitions => Set<ToolDefinition>();
    public DbSet<AgentTool> AgentTools => Set<AgentTool>();
    public DbSet<ProjectDocument> ProjectDocuments => Set<ProjectDocument>();
    public DbSet<AgentConversation> AgentConversations => Set<AgentConversation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AgentTool>().HasKey(x => new { x.AgentId, x.ToolDefinitionId });
        builder.Entity<AgentTool>().HasOne(x => x.Agent).WithMany(x => x.AgentTools).HasForeignKey(x => x.AgentId);
        builder.Entity<AgentTool>().HasOne(x => x.ToolDefinition).WithMany(x => x.AgentTools).HasForeignKey(x => x.ToolDefinitionId);

        builder.Entity<AiModel>().HasIndex(x => x.ModelId);
        builder.Entity<ToolDefinition>().HasIndex(x => new { x.ServiceType, x.MethodName }).IsUnique();
    }
}
