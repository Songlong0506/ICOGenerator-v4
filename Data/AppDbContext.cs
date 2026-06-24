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
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Chuẩn hoá mọi cột DateTime về Kind=Utc khi đọc (xem UtcDateTimeConverter) để JSON trả ra kèm hậu tố 'Z',
        // tránh lệch -7h ở popup AI Call Logs và mọi chỗ hiển thị thời gian khác trên client.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

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
        // decimal(18,6): đủ chỗ cho đơn giá lẻ kiểu $0.075/1M token mà không bị làm tròn về 2 chữ số như mặc định.
        builder.Entity<AiModel>().Property(x => x.InputPricePerMillionTokens).HasPrecision(18, 6);
        builder.Entity<AiModel>().Property(x => x.OutputPricePerMillionTokens).HasPrecision(18, 6);
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
        // Cột WorkflowRunId là khóa nhóm cho báo cáo chi phí "theo run"; KHÔNG khai báo FK để tránh
        // multiple-cascade-path (Project đã cascade cả CallLog lẫn WorkflowRun). Tên run lấy bằng join thủ công khi truy vấn.
        builder.Entity<AgentModelCallLog>().HasIndex(x => x.WorkflowRunId);

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

        // Bound short metadata columns so EF stops mapping them to nvarchar(max) (LOB columns can't be
        // indexed and are slower). Genuinely large fields (Content, RequestJson, ResponseText, Message,
        // Input, Output, Error) are intentionally left as nvarchar(max).
        builder.Entity<Project>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Description).HasMaxLength(2000);
            b.Property(x => x.BackendGitUrl).HasMaxLength(500);
            b.Property(x => x.FrontendGitUrl).HasMaxLength(500);
        });
        builder.Entity<Agent>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(1000);
            b.Property(x => x.Color).HasMaxLength(50);
        });
        builder.Entity<ToolDefinition>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.DisplayName).HasMaxLength(200);
            b.Property(x => x.Description).HasMaxLength(3000);
            b.HasIndex(x => x.Name);
        });
        builder.Entity<WorkflowRun>().Property(x => x.Name).HasMaxLength(200);
        builder.Entity<AgentTask>().Property(x => x.Title).HasMaxLength(300);
        builder.Entity<ProjectDocument>(b =>
        {
            b.Property(x => x.Folder).HasMaxLength(200);
            b.Property(x => x.VersionName).HasMaxLength(100);
            b.Property(x => x.FileName).HasMaxLength(300);
            b.Property(x => x.FilePath).HasMaxLength(1000);
        });
        builder.Entity<AgentModelCallLog>(b =>
        {
            b.Property(x => x.AgentName).HasMaxLength(200);
            b.Property(x => x.ModelName).HasMaxLength(200);
            b.Property(x => x.ModelId).HasMaxLength(200);
            b.Property(x => x.Endpoint).HasMaxLength(500);
            b.Property(x => x.Purpose).HasMaxLength(100);
        });

        // Người dùng đăng nhập: Username là duy nhất, Role lưu dạng chuỗi (dễ đọc trong DB và bền với việc chèn enum mới).
        builder.Entity<AppUser>(b =>
        {
            b.Property(x => x.Username).HasMaxLength(100);
            b.Property(x => x.DisplayName).HasMaxLength(200);
            b.Property(x => x.PasswordHash).HasMaxLength(500);
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(50);
            b.HasIndex(x => x.Username).IsUnique();
        });

        // Bảng cấp quyền: cặp (Role, Permission) là duy nhất; cả hai cột enum lưu dạng chuỗi.
        builder.Entity<RolePermission>(b =>
        {
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(50);
            b.Property(x => x.Permission).HasConversion<string>().HasMaxLength(100);
            b.HasIndex(x => new { x.Role, x.Permission }).IsUnique();
        });
    }
}
