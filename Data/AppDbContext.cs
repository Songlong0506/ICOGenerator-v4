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

        // ⚠️ Hai lambda dưới CAPTURE instance _apiKeyProtector của context ĐẦU TIÊN dựng model (EF cache
        // model toàn cục). AN TOÀN chỉ vì IApiKeyProtector là SINGLETON — ĐỪNG đổi sang Scoped/Transient
        // hay bật AddDbContextPool, sẽ giải mã bằng instance đã dispose/sai.
        builder.Entity<AiModel>().Property(x => x.ApiKey).HasConversion(
            plain => _apiKeyProtector.Protect(plain),
            stored => _apiKeyProtector.Unprotect(stored));
        builder.Entity<Agent>().Property(x => x.RoleKey).HasConversion<string>().HasMaxLength(100);
        builder.Entity<Agent>().HasIndex(x => x.RoleKey);
        // Restrict: không thể xóa model đang được agent sử dụng (DeleteAiModelUseCase đã chặn ở tầng app).
        builder.Entity<Agent>()
            .HasOne(x => x.AiModel)
            .WithMany()
            .HasForeignKey(x => x.AiModelId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ToolDefinition>().HasIndex(x => new { x.ServiceType, x.MethodName }).IsUnique();

        // Audit data: Project FK Cascade, nhưng Agent FK Restrict — KHÔNG để xóa agent wipe sạch lịch sử log/hội thoại của nó.
        builder.Entity<AgentModelCallLog>().HasOne(x => x.Project).WithMany(x => x.ModelCallLogs).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AgentModelCallLog>().HasOne(x => x.Agent).WithMany(x => x.ModelCallLogs).HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AgentModelCallLog>().HasIndex(x => new { x.ProjectId, x.AgentId, x.CreatedAt });

        // Khai báo tường minh để Agent FK là Restrict (cùng lý do AgentModelCallLog), giữ Project FK Cascade.
        builder.Entity<AgentConversation>().HasOne(x => x.Project).WithMany(x => x.Conversations).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AgentConversation>().HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AgentConversation>().Property(x => x.Role).HasMaxLength(50);

        // Status giữ nguyên (đã nvarchar(450) trong index); thu gọn cột enum nvarchar(max) (CurrentStage, Type) để index được.
        builder.Entity<WorkflowRun>().Property(x => x.Status).HasConversion<string>();
        builder.Entity<WorkflowRun>().Property(x => x.CurrentStage).HasConversion<string>().HasMaxLength(50);

        builder.Entity<AgentTask>().Property(x => x.Type).HasConversion<string>().HasMaxLength(50);
        builder.Entity<AgentTask>().Property(x => x.Status).HasConversion<string>();

        builder.Entity<WorkflowRun>().HasOne(x => x.Project).WithMany(x => x.WorkflowRuns).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<WorkflowRun>().HasIndex(x => new { x.ProjectId, x.Status, x.CreatedAt });

        builder.Entity<AgentTask>().HasOne(x => x.WorkflowRun).WithMany(x => x.AgentTasks).HasForeignKey(x => x.WorkflowRunId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AgentTask>().HasOne(x => x.Project).WithMany(x => x.AgentTasks).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AgentTask>().HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<AgentTask>().HasIndex(x => new { x.ProjectId, x.Status, x.CreatedAt });
    }
}
