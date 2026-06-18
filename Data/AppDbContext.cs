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

        // ApiKey được mã hóa khi ghi và giải mã khi đọc nên không bao giờ nằm dạng plaintext trong DB.
        // Đổi giá trị so sánh change-tracking vẫn dựa trên plaintext (CLR side) nên không phát sinh update thừa.
        //
        // ⚠️ Hai lambda dưới CAPTURE instance _apiKeyProtector của context ĐẦU TIÊN dựng model; EF
        // cache model toàn cục theo kiểu context nên mọi context sau dùng lại converter gắn với
        // instance đó. Hiện AN TOÀN chỉ vì IApiKeyProtector đăng ký SINGLETON (xem
        // ApplicationServiceCollectionExtensions.AddIcoGeneratorApplication). ĐỪNG đổi nó sang
        // Scoped/Transient hay bật AddDbContextPool — sẽ giải mã bằng instance đã dispose/sai.
        builder.Entity<AiModel>().Property(x => x.ApiKey).HasConversion(
            plain => _apiKeyProtector.Protect(plain),
            stored => _apiKeyProtector.Unprotect(stored));
        builder.Entity<Agent>().Property(x => x.RoleKey).HasConversion<string>().HasMaxLength(100);
        builder.Entity<Agent>().HasIndex(x => x.RoleKey);
        // Quan hệ bắt buộc: agent luôn phải có AiModel. Restrict để không thể xóa
        // model đang được agent sử dụng (DeleteAiModelUseCase đã chặn ở tầng app).
        builder.Entity<Agent>()
            .HasOne(x => x.AiModel)
            .WithMany()
            .HasForeignKey(x => x.AiModelId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ToolDefinition>().HasIndex(x => new { x.ServiceType, x.MethodName }).IsUnique();

        // Log/hội thoại là dữ liệu audit. Project xóa thì cuốn theo (Cascade) là hợp lý, nhưng
        // KHÔNG để xóa một Agent là wipe sạch lịch sử gọi model/hội thoại của nó — đặt FK Agent
        // là Restrict (chặn xóa agent còn log thay vì âm thầm xóa log).
        builder.Entity<AgentModelCallLog>().HasOne(x => x.Project).WithMany(x => x.ModelCallLogs).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AgentModelCallLog>().HasOne(x => x.Agent).WithMany(x => x.ModelCallLogs).HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AgentModelCallLog>().HasIndex(x => new { x.ProjectId, x.AgentId, x.CreatedAt });

        // AgentConversation trước đây cấu hình hoàn toàn bằng convention → cả hai FK đều Cascade.
        // Khai báo tường minh để Agent FK là Restrict (cùng lý do với AgentModelCallLog ở trên),
        // giữ Project FK là Cascade.
        builder.Entity<AgentConversation>().HasOne(x => x.Project).WithMany(x => x.Conversations).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AgentConversation>().HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AgentConversation>().Property(x => x.Role).HasMaxLength(50);

        // Status đã là nvarchar(450) (nằm trong index) nên giữ nguyên; chỉ thu gọn hai cột enum
        // đang là nvarchar(max) (CurrentStage, Type) xuống độ dài hợp lý để bớt lãng phí và index được.
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
