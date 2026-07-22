using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using ICOGenerator.Services.Tools.Registry;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Migration sinh ra là SQL-Server-specific nên chỉ áp dụng được khi provider là SQL Server. Với
        // provider khác (Sqlite — dùng khi chạy end-to-end ở môi trường không có SQL Server) thì dựng
        // schema trực tiếp từ model bằng EnsureCreated; bỏ qua bảng __EFMigrationsHistory.
        if (db.Database.IsSqlServer())
            await db.Database.MigrateAsync();
        else
            await db.Database.EnsureCreatedAsync();

        await RecoverOrphanedTasksAsync(db);
        await SeedUsersAsync(db);
        await SeedRolePermissionsAsync(db);
        await BackfillSuperAdminAsync(db);
        await SeedOrgUnitsAndAssociatesAsync(db);
        await SeedEvalScenariosAsync(db);

        var discovery = scope.ServiceProvider.GetRequiredService<ToolDiscoveryService>();
        await discovery.SyncToolDefinitionsAsync();

        if (!await db.AiModels.AnyAsync())
        {
            db.AiModels.AddRange(
                new AiModel { ModelId = "qwen3.6-27b@q3_k_s", Endpoint = "http://127.0.0.1:1234/v1", ApiKey = "lm-studio", SupportsVision = false, ContextWindow = 128000 },
                new AiModel { ModelId = "deepseek-v4-flash", Endpoint = "https://api.deepseek.com", ApiKey = "", SupportsVision = true, ContextWindow = 1000000, InputPricePerMillionTokens = 0.14m, OutputPricePerMillionTokens = 0.28m }
            );
            await db.SaveChangesAsync();
        }

        if (!await db.Agents.AnyAsync())
        {
            var modelId = await db.AiModels
                .OrderByDescending(x => x.ModelId == "deepseek-v4-flash")
                .ThenBy(x => x.ModelId)
                .Select(x => x.Id)
                .FirstAsync();
            var agents = new[]
            {
                new Agent { Temperature = 0.3, RoleKey=AgentRoleKey.BusinessAnalyst, Color="#8B5CF6", AiModelId=modelId, Description="Thu thập và phân tích yêu cầu, viết tài liệu đặc tả nghiệp vụ." },
                new Agent { Temperature = 0.2, RoleKey=AgentRoleKey.TechLead, Color="#3B82F6", AiModelId=modelId, Description="Thiết kế kiến trúc và review kỹ thuật." },
                new Agent { Temperature = 0.1, RoleKey=AgentRoleKey.Developer, Color="#10B981", AiModelId=modelId, Description="Sinh source code, build và sửa lỗi." },
                new Agent { Temperature = 0.2, RoleKey=AgentRoleKey.Tester, Color="#2563EB", AiModelId=modelId, Description="Viết test cases và kiểm thử." },
                new Agent { RoleKey=AgentRoleKey.UiUx, Color="#F97316", AiModelId=modelId, Description="Thiết kế flow và wireframe." }
            };
            db.Agents.AddRange(agents);
            await db.SaveChangesAsync();

            await AssignDefaultToolsAsync(db);
        }

    }

    // Bộ tài khoản seed cố định (superadmin/admin/teamdev/user). Không còn mật khẩu: chế độ Local tự đăng nhập
    // bằng tài khoản SuperAdmin (toàn quyền), chế độ IdentityServer đồng bộ user từ SSO. Bốn vai trò seed sẵn
    // để phân quyền chạy ngay.
    private static readonly (string Username, string DisplayName, UserRole Role)[] SeedUsers =
    {
        ("superadmin", "Super Administrator", UserRole.SuperAdmin),
        ("admin",      "Administrator",       UserRole.Admin),
        ("teamdev",    "Team Developer",      UserRole.TeamDev),
        ("user",       "User",                UserRole.User),
    };

    // Seed bộ tài khoản cố định (admin/teamdev/user) nếu DB chưa có user nào.
    private static async Task SeedUsersAsync(AppDbContext db)
    {
        if (await db.AppUsers.AnyAsync())
            return;

        foreach (var seed in SeedUsers)
        {
            db.AppUsers.Add(new AppUser
            {
                Username = seed.Username,
                DisplayName = seed.DisplayName,
                Role = seed.Role
            });
        }

        await db.SaveChangesAsync();
    }

    // Quyền mặc định khi bảng RolePermission còn trống. SuperAdmin KHÔNG cần dòng nào (implicit-all trong
    // PermissionService). Admin: seed sẵn TOÀN BỘ quyền để giữ hành vi "toàn quyền" nhưng nay CHỈNH được.
    // TeamDev: mọi thứ trừ quản trị (Settings + Roles). User: chỉ xem Projects/Requirements.
    private static async Task SeedRolePermissionsAsync(AppDbContext db)
    {
        if (await db.RolePermissions.AnyAsync())
            return;

        var defaults = new (UserRole Role, AppPermission[] Permissions)[]
        {
            (UserRole.Admin, PermissionCatalog.AllPermissions.ToArray()),
            (UserRole.TeamDev, new[]
            {
                AppPermission.ProjectsView, AppPermission.ProjectsCreate, AppPermission.ProjectsViewAll,
                AppPermission.RequirementsView, AppPermission.RequirementsManage,
                AppPermission.AgentsView, AppPermission.AgentsManage, AppPermission.DeliveryAdvance,
                AppPermission.ModelsView, AppPermission.ModelsCreate, AppPermission.ModelsEdit, AppPermission.ModelsDelete,
                AppPermission.UsageView,
                AppPermission.QualityView,
                AppPermission.EvalView, AppPermission.EvalManage,
                AppPermission.FeedbackView, AppPermission.FeedbackManage,
                AppPermission.AuditView
            }),
            (UserRole.User, new[]
            {
                AppPermission.ProjectsView, AppPermission.RequirementsView,
                AppPermission.FeedbackView
            }),
        };

        foreach (var (role, permissions) in defaults)
            foreach (var permission in permissions)
                db.RolePermissions.Add(new RolePermission { Role = role, Permission = permission });


        await db.SaveChangesAsync();
    }

    // Nâng cấp DB đã có dữ liệu (bảng AppUser/RolePermission không còn trống nên hai hàm seed ở trên bỏ qua):
    // trước đây Admin là implicit-all nên KHÔNG có dòng RolePermission nào, và chưa hề có role SuperAdmin.
    // Sau khi Admin chuyển sang "cấu hình được", nếu không xử lý thì Admin sẽ mất sạch quyền và không còn tài
    // khoản toàn quyền. Backfill idempotent (chạy mỗi lần khởi động, an toàn khi lặp):
    //  1) Chưa có user SuperAdmin ⇒ tạo 'superadmin' (luôn có một tài khoản toàn quyền không thể tự khóa).
    //  2) Admin chưa có dòng quyền nào ⇒ cấp TOÀN BỘ quyền để giữ đúng hành vi cũ (giờ đã chỉnh được).
    private static async Task BackfillSuperAdminAsync(AppDbContext db)
    {
        var changed = false;

        if (!await db.AppUsers.AnyAsync(u => u.Role == UserRole.SuperAdmin))
        {
            db.AppUsers.Add(new AppUser
            {
                Username = "superadmin",
                DisplayName = "Super Administrator",
                Role = UserRole.SuperAdmin
            });
            changed = true;
        }

        if (await db.AppUsers.AnyAsync() && !await db.RolePermissions.AnyAsync(x => x.Role == UserRole.Admin))
        {
            foreach (var permission in PermissionCatalog.AllPermissions)
                db.RolePermissions.Add(new RolePermission { Role = UserRole.Admin, Permission = permission });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    // Số lần một task được phép chạy lại sau khi bị gián đoạn bởi restart trước khi bị coi là Failed,
    // để một task liên tục làm crash host không bị re-queue vô hạn.
    private const int MaxTaskAttempts = 3;

    // Sau crash/restart, task còn ở trạng thái Running là "mồ côi" (worker đơn lẻ chưa kịp xử lý lúc khởi động).
    // Re-queue để chạy lại; vượt số lần thử thì đánh Failed. Đây cũng là chỗ khiến cột Attempt có ý nghĩa.
    private static async Task RecoverOrphanedTasksAsync(AppDbContext db)
    {
        var orphaned = await db.AgentTasks
            .Include(x => x.WorkflowRun)
            .Where(x => x.Status == AgentTaskStatus.Running)
            .ToListAsync();
        if (orphaned.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var task in orphaned)
        {
            if (task.Attempt >= MaxTaskAttempts)
            {
                task.Status = AgentTaskStatus.Failed;
                task.Error = "Task bị gián đoạn bởi việc khởi động lại ứng dụng và đã vượt quá số lần thử tối đa.";
                task.FinishedAt = now;
                task.WorkflowRun.Status = WorkflowRunStatus.Failed;
                task.WorkflowRun.CurrentStage = WorkflowStageKey.Failed;
                task.WorkflowRun.FinishedAt = now;
            }
            else
            {
                // Worker chỉ nhặt task Queued. Attempt đã được tăng khi task chạy lần trước.
                task.Status = AgentTaskStatus.Queued;
                task.StartedAt = null;
                if (task.WorkflowRun.Status == WorkflowRunStatus.Running)
                    task.WorkflowRun.Status = WorkflowRunStatus.Queued;
            }
        }

        await db.SaveChangesAsync();
    }

    // Dữ liệu mẫu đồng bộ từ HR_Portal (bảng OrgUnits/Associates) — chỉ seed một lần khi hai bảng còn trống,
    // giữ nguyên Id/giá trị gốc để khớp với dữ liệu thật bên HR_Portal.
    private static async Task SeedOrgUnitsAndAssociatesAsync(AppDbContext db)
    {
        if (!await db.OrgUnits.AnyAsync())
        {
            db.OrgUnits.AddRange(OrgUnitsSeedData.All);
            await db.SaveChangesAsync();
        }

        if (!await db.Associates.AnyAsync())
        {
            db.Associates.AddRange(AssociatesSeedData.All);
            await db.SaveChangesAsync();
        }
    }

    // Golden set mặc định cho Prompt Evals (xem EvalScenariosSeedData) — chỉ seed khi bảng còn trống,
    // để bộ scenario người dùng đã chỉnh/tắt không bị ghi đè ở các lần khởi động sau.
    private static async Task SeedEvalScenariosAsync(AppDbContext db)
    {
        if (await db.EvalScenarios.AnyAsync())
            return;

        db.EvalScenarios.AddRange(EvalScenariosSeedData.Build());
        await db.SaveChangesAsync();
    }

    private static async Task AssignDefaultToolsAsync(AppDbContext db)
    {
        var all = await db.ToolDefinitions.ToListAsync();
        async Task Assign(AgentRoleKey roleKey, params string[] toolNames)
        {
            var agent = await db.Agents.FirstOrDefaultAsync(x => x.RoleKey == roleKey);
            if (agent == null)
                return;
            foreach (var tool in all.Where(x => toolNames.Contains(x.Name)))
                db.AgentTools.Add(new AgentTool { AgentId = agent.Id, ToolDefinitionId = tool.Id });
        }

        await Assign(AgentRoleKey.BusinessAnalyst, "ListFiles", "ReadFile", "WriteFile", "SearchFiles");
        await Assign(AgentRoleKey.TechLead, "ListFiles", "ReadFile", "WriteFile", "GitDiff", "GitStatus");
        await Assign(AgentRoleKey.Developer, "ListFiles", "ReadFile", "WriteFile", "WriteFiles", "ReplaceInFile", "SetPocContent", "AppendPocContent", "SetPocScript", "AppendPocScript", "AuditPocContent", "RunCommand", "GitStatus", "GitCommit", "CreateBranch", "PushBranch", "OpenPullRequest");
        await Assign(AgentRoleKey.Tester, "ListFiles", "ReadFile", "WriteFile", "RunCommand");
        await Assign(AgentRoleKey.UiUx, "WriteFile", "ReadFile", "ListFiles");
        await db.SaveChangesAsync();
    }

}
