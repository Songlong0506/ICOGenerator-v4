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
            db.OrgUnits.AddRange(
                new OrgUnit
                {
                    Id = Guid.Parse("8BA9C19B-B26A-4976-B60D-02EA83BDCE68"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212666"),
                    UpdatedDate = DateTime.Parse("2026-05-29 15:01:29.9372381"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFE2.12",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672627",
                    TargetResponsible = "50672623",
                    TrgtManagerLId = "34183936",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                new OrgUnit
                {
                    Id = Guid.Parse("CAC232E2-F42E-4DB1-BA60-034B34E9A6A0"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213296"),
                    UpdatedDate = DateTime.Parse("2026-04-13 11:28:33.1419204"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.2-F3",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764278",
                    TargetResponsible = "50740821",
                    TrgtManagerLId = "33489047",
                    TypeOrganize = "G",
                    IsDepartment = false
                });
            await db.SaveChangesAsync();
        }

        if (!await db.Associates.AnyAsync())
        {
            db.Associates.AddRange(
                new Associate
                {
                    Id = Guid.Parse("50CCF4D7-3915-4F74-8DF0-00A1939CD65C"),
                    CreatedDate = DateTime.Parse("2025-03-19 12:00:00.6655978"),
                    UpdatedDate = DateTime.Parse("2026-01-02 12:00:00.8284540"),
                    IsDelete = false,
                    PersonalNumber = "35962752",
                    GlobalId = "11954888",
                    DisplayName = "Le Anh Hao",
                    OrgUnitCode = "50920748",
                    OrganizationUnit = "PS/EPC2-VN",
                    Email = "HAO.LEANH@VN.BOSCH.COM",
                    Gender = "1",
                    Position = "Technical Documentation Engineer",
                    StandardWorkingHour = 0,
                    Costcenter = "0000183955",
                    LeadingPerson = "35962752",
                    HiredDate = DateTime.Parse("2025-03-12"),
                    UserId = "LHN9HC",
                    IsIndirect = false
                },
                new Associate
                {
                    Id = Guid.Parse("55D57B86-F3EE-4C50-BB39-00A89223B4D7"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:07.6898918"),
                    UpdatedDate = DateTime.Parse("2026-04-21 11:21:58.8802359"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    PersonalNumber = "33490151",
                    GlobalId = "11215615",
                    DisplayName = "Le Van Trung",
                    OrgUnitCode = "51003053",
                    OrganizationUnit = "HcP/TEF3.3.8",
                    Email = "VANTRUNG.LE@VN.BOSCH.COM",
                    Gender = "1",
                    Position = "Senior Technician - Maintenance Service",
                    StandardWorkingHour = 0,
                    Costcenter = "0000183310",
                    LeadingPerson = "33490151",
                    HiredDate = DateTime.Parse("2016-03-21"),
                    UserId = "EVA1HC",
                    IsIndirect = false,
                    Birthday = DateTime.Parse("1986-04-06")
                },
                new Associate
                {
                    Id = Guid.Parse("3497ABCF-CC3D-4431-A5F2-013401C9997A"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:07.6895740"),
                    UpdatedDate = DateTime.Parse("2026-04-21 11:21:58.8803813"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    PersonalNumber = "33491515",
                    GlobalId = "11338441",
                    DisplayName = "Dang Van Nhat Huy",
                    OrgUnitCode = "50151518",
                    OrganizationUnit = "HcP/HRL",
                    Email = "HUY.DANGVANNHAT@VN.BOSCH.COM",
                    Gender = "1",
                    Position = "Manager - HRBP cum Recruitment",
                    StandardWorkingHour = 0,
                    Costcenter = "0000183010",
                    LeadingPerson = "33491515",
                    HiredDate = DateTime.Parse("2017-04-17"),
                    UserId = "HGD1HC",
                    IsIndirect = true,
                    Birthday = DateTime.Parse("1991-08-01")
                },
                new Associate
                {
                    Id = Guid.Parse("527E584C-003E-4350-B336-0141C0F656FF"),
                    CreatedDate = DateTime.Parse("2026-02-06 12:00:00.6811889"),
                    UpdatedDate = DateTime.Parse("2026-06-16 12:00:00.7046670"),
                    IsDelete = false,
                    PersonalNumber = "36208273",
                    GlobalId = "11991011",
                    DisplayName = "Nguyen Huynh Minh Phu",
                    OrgUnitCode = "51008667",
                    OrganizationUnit = "PS-CC/EAD-VN",
                    Email = "FIXED-TERM.PHU.NGUYENHUYNHMINH@VN.BOSCH.COM",
                    Gender = "1",
                    Position = "Intern",
                    StandardWorkingHour = 0,
                    Costcenter = "0000183960",
                    LeadingPerson = "36208273",
                    HiredDate = DateTime.Parse("2026-02-05"),
                    UserId = "PGY6HC",
                    IsIndirect = false
                },
                new Associate
                {
                    Id = Guid.Parse("89295735-AAB1-4604-A54B-0158F8458489"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:07.6898831"),
                    UpdatedDate = DateTime.Parse("2026-04-21 11:21:58.8801473"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    PersonalNumber = "33489270",
                    GlobalId = "11130636",
                    DisplayName = "Le Trung Hieu",
                    OrgUnitCode = "50610498",
                    OrganizationUnit = "HcP/TEF3.3.2",
                    Email = "HIEU.LETRUNG@BOSCH.COM",
                    Gender = "1",
                    Position = "Senior Technician - Maintenance",
                    StandardWorkingHour = 0,
                    Costcenter = "0000183310",
                    LeadingPerson = "33489270",
                    HiredDate = DateTime.Parse("2015-03-30"),
                    UserId = "LIH1HC",
                    IsIndirect = false,
                    Birthday = DateTime.Parse("1990-11-25")
                });
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
