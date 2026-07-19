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
    public DbSet<ProjectDocumentRevision> ProjectDocumentRevisions => Set<ProjectDocumentRevision>();
    public DbSet<ProjectSourceFile> ProjectSourceFiles => Set<ProjectSourceFile>();
    public DbSet<AgentConversation> AgentConversations => Set<AgentConversation>();
    public DbSet<AgentModelCallLog> AgentModelCallLogs => Set<AgentModelCallLog>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<AgentTask> AgentTasks => Set<AgentTask>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Feedback> Feedbacks => Set<Feedback>();
    public DbSet<FeedbackAttachment> FeedbackAttachments => Set<FeedbackAttachment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OrgUnit> OrgUnits => Set<OrgUnit>();
    public DbSet<Associate> Associates => Set<Associate>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<EvalScenario> EvalScenarios => Set<EvalScenario>();
    public DbSet<EvalRun> EvalRuns => Set<EvalRun>();
    public DbSet<EvalResult> EvalResults => Set<EvalResult>();
    public DbSet<PromptTemplateVersion> PromptTemplateVersions => Set<PromptTemplateVersion>();
    public DbSet<PocComment> PocComments => Set<PocComment>();
    public DbSet<AgentDomainChecklistNote> AgentDomainChecklistNotes => Set<AgentDomainChecklistNote>();

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
        // RoleKey là danh tính của agent: mọi lookup (BAAgentResolver, WorkflowOrchestrator, AgentTaskWorker,
        // ApproveStageUseCase, …) đều dùng FirstOrDefault(x => x.RoleKey == …) và ngầm định mỗi role đúng một
        // agent. Unique để DB ép ràng buộc đó — trước đây cột Name tự do dùng để phân biệt agent trùng role.
        builder.Entity<Agent>().HasIndex(x => x.RoleKey).IsUnique();
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

        // Nội dung hội thoại user↔agent được mã hóa AT REST (cùng cơ chế/khóa với ApiKey — xem cảnh báo
        // SINGLETON ở converter AiModel.ApiKey bên trên). RequestJson/ResponseText/ErrorMessage của log lời gọi
        // LLM chứa lại toàn bộ transcript nên phải mã hóa CÙNG LÚC với AgentConversation.Message, nếu không việc
        // mã hóa là vô nghĩa. Dữ liệu cũ (không có tiền tố "enc:v1:") vẫn đọc được nhờ Unprotect tương thích ngược.
        // Đánh đổi: KHÔNG còn search/LIKE/order các cột này ở tầng SQL (ciphertext) — mọi lọc phải làm sau khi
        // materialize (LINQ-to-Objects), như IsVerifiedInviteLatestTurn. Cột vẫn nvarchar(max) nên không đổi schema.
        builder.Entity<AgentModelCallLog>().Property(x => x.RequestJson).HasConversion(
            plain => _apiKeyProtector.Protect(plain),
            stored => _apiKeyProtector.Unprotect(stored));
        builder.Entity<AgentModelCallLog>().Property(x => x.ResponseText).HasConversion(
            plain => _apiKeyProtector.Protect(plain),
            stored => _apiKeyProtector.Unprotect(stored));
        builder.Entity<AgentModelCallLog>().Property(x => x.ErrorMessage).HasConversion(
            plain => _apiKeyProtector.Protect(plain),
            stored => _apiKeyProtector.Unprotect(stored));

        // Khai báo tường minh để Agent FK là Restrict (cùng lý do AgentModelCallLog), giữ Project FK Cascade.
        builder.Entity<AgentConversation>().HasOne(x => x.Project).WithMany(x => x.Conversations).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        // "New Chat" LƯU TRỮ hội thoại cũ (ArchivedAt != null) thay vì xóa cứng. Global filter để MỌI
        // query lẫn Include (UI, memory, transcript, con trỏ harvest) chỉ thấy hội thoại hiện hành —
        // không đường đọc nào có thể quên lọc. Cần đọc bản lưu trữ thì dùng IgnoreQueryFilters().
        builder.Entity<AgentConversation>().HasQueryFilter(x => x.ArchivedAt == null);

        // Checklist học được theo miền: mỗi (agent, miền) đúng MỘT bucket — index unique để hai vòng
        // harvest song song không tạo bucket đôi. Xem ChecklistNoteStore.
        builder.Entity<AgentDomainChecklistNote>().Property(x => x.DomainKey).HasMaxLength(40);
        builder.Entity<AgentDomainChecklistNote>().HasIndex(x => new { x.AgentId, x.DomainKey }).IsUnique();
        builder.Entity<AgentDomainChecklistNote>().HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AgentConversation>().HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AgentConversation>().Property(x => x.Role).HasMaxLength(50);
        // Message = text lượt chat; Suggestions = JSON các chip gợi ý (cũng là nội dung hội thoại). Mã hóa at rest
        // (xem ghi chú converter của AgentModelCallLog bên trên). Suggestions nullable: EF giữ null nguyên trạng,
        // converter chỉ chạy với giá trị non-null.
        builder.Entity<AgentConversation>().Property(x => x.Message).HasConversion(
            plain => _apiKeyProtector.Protect(plain),
            stored => _apiKeyProtector.Unprotect(stored));
        builder.Entity<AgentConversation>().Property(x => x.Suggestions).HasConversion(
            plain => _apiKeyProtector.Protect(plain),
            stored => _apiKeyProtector.Unprotect(stored));
        builder.Entity<AgentConversation>().Property(x => x.FlowDiagram).HasConversion(
            plain => _apiKeyProtector.Protect(plain),
            stored => _apiKeyProtector.Unprotect(stored));

        // Status giữ nguyên (đã nvarchar(450) trong index); thu gọn cột enum nvarchar(max) (CurrentStage, Type) để index được.
        // Status là CONCURRENCY TOKEN (không đổi schema — chỉ thêm "AND Status = @original" vào mọi UPDATE):
        // hai người cùng bấm Duyệt/Chỉnh sửa một cổng WaitingForHuman sẽ không còn enqueue ĐÔI task (đốt token
        // gấp đôi, output ghi đè nhau) — lần lưu thua ném DbUpdateConcurrencyException và use case cổng duyệt
        // trả "không còn bước chờ duyệt". Chọn cách này thay vì rowversion vì nó chạy được trên CẢ Sqlite
        // (rowversion là kiểu SQL-Server-specific, mà test/CI chạy Sqlite).
        builder.Entity<WorkflowRun>().Property(x => x.Status).HasConversion<string>().IsConcurrencyToken();
        builder.Entity<WorkflowRun>().Property(x => x.CurrentStage).HasConversion<string>().HasMaxLength(50);

        builder.Entity<AgentTask>().Property(x => x.Type).HasConversion<string>().HasMaxLength(50);
        // Concurrency token (như WorkflowRun.Status): cho phép worker "claim" task Queued → Running một cách
        // nguyên tử — hai vòng dispatch (hoặc hai instance app trong tương lai) cùng nhặt một task thì chỉ
        // một bên thắng, bên thua nhận DbUpdateConcurrencyException và bỏ qua task đó.
        builder.Entity<AgentTask>().Property(x => x.Status).HasConversion<string>().IsConcurrencyToken();

        builder.Entity<WorkflowRun>().HasOne(x => x.Project).WithMany(x => x.WorkflowRuns).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<WorkflowRun>().HasIndex(x => new { x.ProjectId, x.Status, x.CreatedAt });

        builder.Entity<AgentTask>().HasOne(x => x.WorkflowRun).WithMany(x => x.AgentTasks).HasForeignKey(x => x.WorkflowRunId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AgentTask>().HasOne(x => x.Project).WithMany(x => x.AgentTasks).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AgentTask>().HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.SetNull);
        builder.Entity<AgentTask>().HasIndex(x => new { x.ProjectId, x.Status, x.CreatedAt });
        // AgentTaskWorker poll bảng này MỖI 2 GIÂY với WHERE Status == Queued ORDER BY CreatedAt (KHÔNG lọc
        // ProjectId), nên index (ProjectId, Status, CreatedAt) ở trên — leading column ProjectId — không seek
        // được cho query đó và SQL Server phải scan (chi phí tăng dần khi bảng tích lũy task lịch sử). Index
        // (Status, CreatedAt) này biến poll thành một seek + lấy dòng cũ nhất ở ngay đầu range.
        builder.Entity<AgentTask>().HasIndex(x => new { x.Status, x.CreatedAt });

        // Bound short metadata columns so EF stops mapping them to nvarchar(max) (LOB columns can't be
        // indexed and are slower). Genuinely large fields (Content, RequestJson, ResponseText, Message,
        // Input, Output, Error) are intentionally left as nvarchar(max).
        builder.Entity<Project>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Description).HasMaxLength(2000);
            b.Property(x => x.BackendGitUrl).HasMaxLength(500);
            b.Property(x => x.FrontendGitUrl).HasMaxLength(500);
            b.Property(x => x.CreatedByUsername).HasMaxLength(100);
            // Cùng cỡ với OrgUnit.OrgUnitCode — cột này lưu mã tra sang bảng OrgUnits (không FK: dữ liệu
            // HR đồng bộ lại có thể xóa/tạo lại orgUnit, project cũ vẫn giữ mã như một nhãn lịch sử).
            b.Property(x => x.OrgUnitCode).HasMaxLength(50);
            // Lọc danh sách project theo chủ sở hữu (User thường) là truy vấn nóng ở trang Projects/Index.
            b.HasIndex(x => new { x.CreatedByUsername, x.CreatedAt });
        });
        builder.Entity<Agent>(b =>
        {
            b.Property(x => x.Description).HasMaxLength(1000);
            b.Property(x => x.Color).HasMaxLength(50);
            b.Property(x => x.CreatedByUsername).HasMaxLength(100);
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

        // Lịch sử nội dung tài liệu: Document FK Cascade (xóa document ⇒ dọn luôn lịch sử; chuỗi cascade
        // Project → Documents → Revisions là một đường duy nhất nên SQL Server không kêu multiple-path).
        // Content để nvarchar(max) (LOB); (DocumentId, RevisionNumber) duy nhất — cũng là index cho truy
        // vấn nóng "liệt kê revision của một document theo thứ tự".
        builder.Entity<ProjectDocumentRevision>(b =>
        {
            b.HasOne(x => x.ProjectDocument).WithMany(x => x.Revisions).HasForeignKey(x => x.ProjectDocumentId).OnDelete(DeleteBehavior.Cascade);
            b.Property(x => x.ChangeNote).HasMaxLength(500);
            b.Property(x => x.VersionName).HasMaxLength(100);
            b.HasIndex(x => new { x.ProjectDocumentId, x.RevisionNumber }).IsUnique();
        });

        // Tài liệu nguồn user upload: Project FK Cascade (xóa project ⇒ dọn luôn các nguồn). Kind lưu dạng chuỗi
        // (dễ đọc, bền với việc chèn enum mới). ExtractedText để nvarchar(max) (LOB), còn lại bound.
        builder.Entity<ProjectSourceFile>(b =>
        {
            b.HasOne(x => x.Project).WithMany(x => x.SourceFiles).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.FileName).HasMaxLength(300);
            b.Property(x => x.ContentType).HasMaxLength(150);
            b.Property(x => x.StoredPath).HasMaxLength(1000);
            b.Property(x => x.UploadedByUserId).HasMaxLength(100);
            b.HasIndex(x => new { x.ProjectId, x.CreatedAt });
        });
        builder.Entity<AgentModelCallLog>(b =>
        {
            b.Property(x => x.AgentName).HasMaxLength(200);
            b.Property(x => x.ModelId).HasMaxLength(200);
            b.Property(x => x.Purpose).HasMaxLength(100);
            // BudgetGuard chạy TRƯỚC MỖI lời gọi model: WHERE CreatedAt >= @windowStart GROUP BY
            // (ProjectId, ModelId) SUM(token). Không có index theo CreatedAt thì mỗi lời gọi là một
            // lượt scan cả bảng log (chỉ-tăng-không-giảm). Index này biến nó thành một range-seek;
            // IncludeProperties phủ luôn các cột guard cần đọc nên khỏi lookup về clustered index
            // (SQL Server; Sqlite bỏ qua include — vẫn dùng được phần key CreatedAt).
            b.HasIndex(x => x.CreatedAt)
                .IncludeProperties(x => new { x.ProjectId, x.ModelId, x.PromptTokens, x.CompletionTokens });
        });

        // Người dùng đăng nhập: Username là duy nhất, Role lưu dạng chuỗi (dễ đọc trong DB và bền với việc chèn enum mới).
        builder.Entity<AppUser>(b =>
        {
            b.Property(x => x.Username).HasMaxLength(100);
            b.Property(x => x.DisplayName).HasMaxLength(200);
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(50);
            b.Property(x => x.OrgUnitName).HasMaxLength(200);
            b.Property(x => x.Email).HasMaxLength(200);
            // HasDefaultValue để install cũ (migration thêm cột) có giá trị hợp lý cho các dòng sẵn có,
            // giữ nguyên hành vi: vẫn nhận chuông cho mọi loại sự kiện, email cá nhân tắt.
            b.Property(x => x.NotifyInApp).HasDefaultValue(true);
            b.Property(x => x.NotifyByEmail).HasDefaultValue(false);
            b.Property(x => x.NotifyOnGate).HasDefaultValue(true);
            b.Property(x => x.NotifyOnCompleted).HasDefaultValue(true);
            b.Property(x => x.NotifyOnFailed).HasDefaultValue(true);
            b.HasIndex(x => x.Username).IsUnique();
        });

        // Bảng cấp quyền: cặp (Role, Permission) là duy nhất; cả hai cột enum lưu dạng chuỗi.
        builder.Entity<RolePermission>(b =>
        {
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(50);
            b.Property(x => x.Permission).HasConversion<string>().HasMaxLength(100);
            b.HasIndex(x => new { x.Role, x.Permission }).IsUnique();
        });

        // Phản hồi người dùng (toàn app, không gắn project). Type/Status lưu dạng chuỗi (dễ đọc, bền với việc
        // chèn enum mới). Message để nvarchar(max) (LOB), các cột metadata còn lại bound để index/nhẹ hơn.
        builder.Entity<Feedback>(b =>
        {
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Title).HasMaxLength(200);
            b.Property(x => x.CreatedByUsername).HasMaxLength(100);
            b.Property(x => x.SubmittedByName).HasMaxLength(200);
            b.HasIndex(x => x.CreatedAt);
            b.HasIndex(x => new { x.CreatedByUsername, x.CreatedAt });
        });

        // File đính kèm: Feedback FK Cascade (xóa phản hồi ⇒ dọn luôn metadata file). Kind lưu dạng chuỗi.
        builder.Entity<FeedbackAttachment>(b =>
        {
            b.HasOne(x => x.Feedback).WithMany(x => x.Attachments).HasForeignKey(x => x.FeedbackId).OnDelete(DeleteBehavior.Cascade);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.FileName).HasMaxLength(300);
            b.Property(x => x.ContentType).HasMaxLength(150);
            b.Property(x => x.StoredPath).HasMaxLength(1000);
            b.HasIndex(x => x.FeedbackId);
        });

        // Nhật ký thay đổi cấu hình (Settings/Role/Agent/Model). Category/Action lưu dạng chuỗi (dễ đọc, bền
        // với việc chèn enum mới). Before/AfterJson để nvarchar(max) (LOB), các cột metadata còn lại bound để
        // index/nhẹ hơn. Index theo (Category, CreatedAt) và CreatedAt cho lọc + sắp xếp ở trang Audit Log.
        builder.Entity<AuditLog>(b =>
        {
            b.Property(x => x.Category).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.EntityId).HasMaxLength(100);
            b.Property(x => x.Summary).HasMaxLength(500);
            b.Property(x => x.ActorUsername).HasMaxLength(100);
            b.Property(x => x.ActorRole).HasMaxLength(50);
            b.HasIndex(x => x.CreatedAt);
            b.HasIndex(x => new { x.Category, x.CreatedAt });
        });

        // Thông báo in-app: Type lưu dạng chuỗi (dễ đọc, bền với việc chèn enum mới). Message để nvarchar(max)
        // (LOB), các cột metadata còn lại bound để nhẹ hơn. Index (RecipientUsername, IsRead, CreatedAt) phục vụ
        // truy vấn nóng của chuông: đếm chưa đọc + lấy N thông báo mới nhất của một người.
        builder.Entity<Notification>(b =>
        {
            b.Property(x => x.RecipientUsername).HasMaxLength(100);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Title).HasMaxLength(300);
            b.Property(x => x.ProjectName).HasMaxLength(200);
            b.Property(x => x.Link).HasMaxLength(1000);
            b.HasIndex(x => new { x.RecipientUsername, x.IsRead, x.CreatedAt });
        });

        // Prompt eval harness: scenario (golden set) / run / kết quả từng scenario. Status lưu dạng chuỗi
        // (dễ đọc, bền với việc chèn enum mới); các trường LỚN (UserInput/Criteria/Output/Reasoning/Error)
        // để nvarchar(max), metadata bound. Model & scenario tham chiếu bằng Guid KHÔNG FK (xem chú thích
        // trên entity); chỉ EvalResult → EvalRun là FK thật (Cascade: xoá run dọn luôn kết quả).
        builder.Entity<EvalScenario>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.PromptKey).HasMaxLength(300);
            b.Property(x => x.CreatedByUsername).HasMaxLength(100);
            b.HasIndex(x => new { x.IsActive, x.CreatedAt });
        });
        builder.Entity<EvalRun>(b =>
        {
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.Note).HasMaxLength(500);
            b.Property(x => x.PromptKey).HasMaxLength(300);
            b.Property(x => x.TargetModelName).HasMaxLength(200);
            b.Property(x => x.JudgeModelName).HasMaxLength(200);
            b.Property(x => x.CreatedByUsername).HasMaxLength(100);
            // decimal(18,6): chi phí eval hay nhỏ dưới cent (giống đơn giá $/1M token), tránh làm tròn về 2 chữ số.
            b.Property(x => x.TotalCost).HasPrecision(18, 6);
            // Worker poll run Queued cũ nhất + trang Eval liệt kê run mới nhất: cùng một index phục vụ cả hai.
            b.HasIndex(x => new { x.Status, x.CreatedAt });
            b.HasIndex(x => x.CreatedAt);
        });
        builder.Entity<EvalResult>(b =>
        {
            b.HasOne(x => x.EvalRun).WithMany(x => x.Results).HasForeignKey(x => x.EvalRunId).OnDelete(DeleteBehavior.Cascade);
            b.Property(x => x.ScenarioName).HasMaxLength(200);
            b.Property(x => x.TargetCost).HasPrecision(18, 6);
            b.Property(x => x.JudgeCost).HasPrecision(18, 6);
            b.HasIndex(x => x.EvalRunId);
            b.HasIndex(x => x.EvalScenarioId);
        });

        // Phiên bản prompt chỉnh runtime (Prompt Studio): Content là snapshot đầy đủ (nvarchar(max)),
        // metadata bound. (PromptKey, VersionNumber) duy nhất — cũng là index cho "lịch sử một template";
        // (PromptKey, IsActive) phục vụ truy vấn nóng "bản active của mọi template" mà provider cache lại.
        builder.Entity<PromptTemplateVersion>(b =>
        {
            b.Property(x => x.PromptKey).HasMaxLength(300);
            b.Property(x => x.ChangeNote).HasMaxLength(500);
            b.Property(x => x.CreatedByUsername).HasMaxLength(100);
            b.HasIndex(x => new { x.PromptKey, x.VersionNumber }).IsUnique();
            b.HasIndex(x => new { x.PromptKey, x.IsActive });
        });

        // Ghi chú ghim trên POC: Project FK Cascade (xóa project ⇒ dọn luôn ghi chú). Status lưu dạng
        // chuỗi (dễ đọc, bền với việc chèn enum mới); Comment bound 4000 (một ghi chú review, không phải
        // tài liệu). Index (ProjectId, Status, CreatedAt) phục vụ truy vấn nóng "ghi chú Open của một
        // project theo thứ tự" ở trang PocReview và khi gom vào yêu cầu chỉnh sửa.
        builder.Entity<PocComment>(b =>
        {
            b.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.PageView).HasMaxLength(200);
            b.Property(x => x.ElementLabel).HasMaxLength(300);
            b.Property(x => x.ElementPath).HasMaxLength(600);
            b.Property(x => x.Comment).HasMaxLength(4000);
            b.Property(x => x.CreatedByUsername).HasMaxLength(100);
            b.HasIndex(x => new { x.ProjectId, x.Status, x.CreatedAt });
        });

        // Dữ liệu tổ chức đồng bộ từ HR_Portal (bảng OrgUnits/Associates): OrgUnitCode là khóa tra cứu
        // chính giữa hai bảng nên đánh index cho cả hai; các cột decimal cần khai precision tường minh.
        builder.Entity<OrgUnit>().HasIndex(x => x.OrgUnitCode);
        builder.Entity<Associate>(b =>
        {
            b.Property(x => x.StandardWorkingHour).HasPrecision(18, 2);
            b.HasIndex(x => x.OrgUnitCode);
            b.HasIndex(x => x.GlobalId);
        });
    }
}
