using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using ICOGenerator.Services.Tools.Registry;
using Microsoft.AspNetCore.Identity;
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
        await SeedUsersAsync(db, scope.ServiceProvider);
        await SeedRolePermissionsAsync(db);
        await SeedOrgUnitsAndAssociatesAsync(db);

        var discovery = scope.ServiceProvider.GetRequiredService<ToolDiscoveryService>();
        await discovery.SyncToolDefinitionsAsync();

        if (!await db.AiModels.AnyAsync())
        {
            db.AiModels.AddRange(
                new AiModel { Name = "Qwen3.6 27B Q3_K_S", Provider = "LM Studio", ModelId = "qwen3.6-27b@q3_k_s", Endpoint = "http://127.0.0.1:1234/v1", ApiKey = "lm-studio", ContextWindow = 128000 },
                new AiModel { Name = "DeepSeek V4 Flash", Provider = "DeepSeek", ModelId = "deepseek-v4-flash", Endpoint = "https://api.deepseek.com", ApiKey = "", ContextWindow = 1000000, InputPricePerMillionTokens = 0.14m, OutputPricePerMillionTokens = 0.28m }
            );
            await db.SaveChangesAsync();
        }

        if (!await db.Agents.AnyAsync())
        {
            var modelId = await db.AiModels
                .OrderByDescending(x => x.Name == "DeepSeek V4 Flash")
                .ThenBy(x => x.Name)
                .Select(x => x.Id)
                .FirstAsync();
            var agents = new[]
            {
                new Agent { Name="BA", Temperature = 0.3, RoleKey=AgentRoleKey.BusinessAnalyst, Color="#8B5CF6", AiModelId=modelId, Description="Thu thập và phân tích yêu cầu, viết tài liệu đặc tả nghiệp vụ." },
                new Agent { Name="Tech Lead", Temperature = 0.2, RoleKey=AgentRoleKey.TechLead, Color="#3B82F6", AiModelId=modelId, Description="Thiết kế kiến trúc và review kỹ thuật." },
                new Agent { Name="Developer", Temperature = 0.1, RoleKey=AgentRoleKey.Developer, Color="#10B981", AiModelId=modelId, Description="Sinh source code, build và sửa lỗi." },
                new Agent { Name="Tester", Temperature = 0.2, RoleKey=AgentRoleKey.Tester, Color="#2563EB", AiModelId=modelId, Description="Viết test cases và kiểm thử." },
                new Agent { Name="UI/UX", RoleKey=AgentRoleKey.UiUx, Color="#F97316", AiModelId=modelId, Description="Thiết kế flow và wireframe." }
            };
            db.Agents.AddRange(agents);
            await db.SaveChangesAsync();

            await AssignDefaultToolsAsync(db);
        }

    }

    // Bộ tài khoản seed cố định (admin/teamdev/user) cùng mật khẩu mặc định — chỉ dùng cho lần khởi tạo đầu.
    // ⚠️ Mật khẩu mặc định chỉ hợp cho môi trường nội bộ/dev; đổi ngay sau lần đăng nhập đầu trên môi trường thật.
    private static readonly (string Username, string DisplayName, UserRole Role, string DefaultPassword)[] SeedUsers =
    {
        ("admin",   "Administrator",  UserRole.Admin,   "Admin@123"),
        ("teamdev", "Team Developer", UserRole.TeamDev, "TeamDev@123"),
        ("user",    "User",           UserRole.User,    "User@123"),
    };

    // Seed bộ tài khoản cố định (admin/teamdev/user) nếu DB chưa có user nào. Mật khẩu được băm bằng
    // PasswordHasher của ASP.NET rồi lưu vào bảng AppUser. Dùng mật khẩu mặc định và ghi cảnh báo để
    // nhắc đổi trên môi trường thật.
    private static async Task SeedUsersAsync(AppDbContext db, IServiceProvider provider)
    {
        if (await db.AppUsers.AnyAsync())
            return;

        var hasher = provider.GetRequiredService<IPasswordHasher<AppUser>>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DbInitializer));

        logger.LogWarning(
            "Seed tài khoản (admin/teamdev/user) dùng MẬT KHẨU MẶC ĐỊNH. Hãy đổi mật khẩu ngay sau lần đăng nhập đầu trên môi trường thật.");

        foreach (var seed in SeedUsers)
        {
            var user = new AppUser
            {
                Username = seed.Username,
                DisplayName = seed.DisplayName,
                Role = seed.Role,
                IsActive = true
            };
            user.PasswordHash = hasher.HashPassword(user, seed.DefaultPassword);
            db.AppUsers.Add(user);
        }

        await db.SaveChangesAsync();
    }

    // Quyền mặc định khi bảng RolePermission còn trống. Admin KHÔNG cần dòng nào (implicit-all trong
    // PermissionService). TeamDev: mọi thứ trừ quản trị (Settings + Roles). User: chỉ xem Projects/Requirements.
    private static async Task SeedRolePermissionsAsync(AppDbContext db)
    {
        if (await db.RolePermissions.AnyAsync())
            return;

        var defaults = new (UserRole Role, AppPermission[] Permissions)[]
        {
            (UserRole.TeamDev, new[]
            {
                AppPermission.ProjectsView, AppPermission.ProjectsCreate, AppPermission.ProjectsViewAll,
                AppPermission.RequirementsView, AppPermission.RequirementsManage,
                AppPermission.AgentsView, AppPermission.AgentsManage, AppPermission.DeliveryAdvance,
                AppPermission.ModelsView, AppPermission.ModelsCreate, AppPermission.ModelsEdit, AppPermission.ModelsDelete,
                AppPermission.UsageView,
                AppPermission.QualityView, AppPermission.QualityManage,
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
