using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Data;

public class AppDbContext : DbContext
{
    private readonly IApiKeyProtector _apiKeyProtector;

    public AppDbContext(DbContextOptions<AppDbContext> options, IApiKeyProtector apiKeyProtector) : base(options)
        => _apiKeyProtector = apiKeyProtector;

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AiModel> AiModels => Set<AiModel>();
    public DbSet<ToolDefinition> ToolDefinitions => Set<ToolDefinition>();
    public DbSet<AgentTool> AgentTools => Set<AgentTool>();
    public DbSet<ProjectDocument> ProjectDocuments => Set<ProjectDocument>();
    public DbSet<AgentConversation> AgentConversations => Set<AgentConversation>();
    public DbSet<AgentJob> AgentJobs => Set<AgentJob>();
    public DbSet<AgentModelCallLog> AgentModelCallLogs => Set<AgentModelCallLog>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<AgentTask> AgentTasks => Set<AgentTask>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AgentTool>().HasKey(x => new { x.AgentId, x.ToolDefinitionId });
        builder.Entity<AgentTool>().HasOne(x => x.Agent).WithMany(x => x.AgentTools).HasForeignKey(x => x.AgentId);
        builder.Entity<AgentTool>().HasOne(x => x.ToolDefinition).WithMany(x => x.AgentTools).HasForeignKey(x => x.ToolDefinitionId);

        builder.Entity<AiModel>().HasIndex(x => x.ModelId);

        // ApiKey được mã hóa khi ghi và giải mã khi đọc nên không bao giờ nằm dạng plaintext trong DB.
        // Đổi giá trị so sánh change-tracking vẫn dựa trên plaintext (CLR side) nên không phát sinh update thừa.
        builder.Entity<AiModel>().Property(x => x.ApiKey).HasConversion(
            plain => _apiKeyProtector.Protect(plain),
            stored => _apiKeyProtector.Unprotect(stored));
        builder.Entity<Agent>().Property(x => x.RoleKey).HasConversion<string>().HasMaxLength(100);
        builder.Entity<Agent>().HasIndex(x => x.RoleKey);
        builder.Entity<ToolDefinition>().HasIndex(x => new { x.ServiceType, x.MethodName }).IsUnique();

        builder.Entity<AgentModelCallLog>().HasOne(x => x.Project).WithMany(x => x.ModelCallLogs).HasForeignKey(x => x.ProjectId);
        builder.Entity<AgentModelCallLog>().HasOne(x => x.Agent).WithMany(x => x.ModelCallLogs).HasForeignKey(x => x.AgentId);
        builder.Entity<AgentModelCallLog>().HasIndex(x => new { x.ProjectId, x.AgentId, x.CreatedAt });

        builder.Entity<WorkflowRun>().Property(x => x.Status).HasConversion<string>();
        builder.Entity<WorkflowRun>().Property(x => x.CurrentStage).HasConversion<string>();

        builder.Entity<AgentTask>().Property(x => x.Type).HasConversion<string>();
        builder.Entity<AgentTask>().Property(x => x.Status).HasConversion<string>();

        builder.Entity<AgentJob>().Property(x => x.Status).HasConversion<string>();

        builder.Entity<WorkflowRun>().HasOne(x => x.Project).WithMany(x => x.WorkflowRuns).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<WorkflowRun>().HasIndex(x => new { x.ProjectId, x.Status, x.CreatedAt });

        builder.Entity<AgentTask>().HasOne(x => x.WorkflowRun).WithMany(x => x.AgentTasks).HasForeignKey(x => x.WorkflowRunId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AgentTask>().HasOne(x => x.Project).WithMany(x => x.AgentTasks).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AgentTask>().HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<AgentTask>().HasIndex(x => new { x.ProjectId, x.Status, x.CreatedAt });
    }
}
