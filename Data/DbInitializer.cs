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
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("74D8C8C4-F5F5-4FCF-BA48-0432CB2230F1"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213306"),
                    UpdatedDate = DateTime.Parse("2026-05-04 12:00:01.8920710"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.3-F2.1",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764295",
                    TargetResponsible = "50740822",
                    TrgtManagerLId = "33488431",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("2ED037DE-EF71-44D1-BE42-0672D9E6075F"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213711"),
                    UpdatedDate = DateTime.Parse("2026-05-29 14:01:33.6736873"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS/QMM7-HcP",
                    CostCenter = "00001834000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50407646",
                    TargetResponsible = "50151573",
                    TrgtManagerLId = "33488672",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3DD4AB29-D7F2-4248-A4BB-0803E86E6CD4"),
                    CreatedDate = DateTime.Parse("2026-04-01 12:00:01.4416026"),
                    UpdatedDate = DateTime.Parse("2026-04-01 12:00:01.4416334"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-BACKUP-B",
                    CostCenter = "00001835200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51007890",
                    TargetResponsible = "50672596",
                    TrgtManagerLId = "33488413",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("A0ACE034-CF9F-4DB6-B081-093A38221D19"),
                    CreatedDate = DateTime.Parse("2025-06-06 12:00:01.5297405"),
                    UpdatedDate = DateTime.Parse("2025-11-02 12:00:02.8100547"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GS/OSD3-APAC211",
                    CostCenter = "C1820004040010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51002720",
                    TargetResponsible = "50823896",
                    TrgtManagerLId = "33489234",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("F7F055DC-3612-4433-9476-0B1A1A936C1E"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213436"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:14:16.0598521"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.2",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50599025",
                    TargetResponsible = "50415391",
                    TrgtManagerLId = "33497797",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D5908F01-C712-4B76-8D10-0C0005EEC084"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213647"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:14:20.1459486"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS/QMM3.2-A-HcP",
                    CostCenter = "00001834400010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50742962",
                    TargetResponsible = "50407643",
                    TrgtManagerLId = "33489519",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("5E698E09-275A-4261-A084-0C4EB85050E9"),
                    CreatedDate = DateTime.Parse("2026-04-01 12:00:01.4416354"),
                    UpdatedDate = DateTime.Parse("2026-04-01 12:00:01.4416361"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-BACKUP-C",
                    CostCenter = "00001835200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51007891",
                    TargetResponsible = "50672596",
                    TrgtManagerLId = "33496752",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("2C30DAEB-0E00-4518-BA4E-0DE27142319A"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213716"),
                    UpdatedDate = DateTime.Parse("2026-06-04 10:49:13.0762764"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS/QMM-HcP",
                    CostCenter = "00001834000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50151573",
                    TargetResponsible = "50940542",
                    TrgtManagerLId = "33770312",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("C7CDB3C9-014A-4DB4-8465-0EA432D2B5EC"),
                    CreatedDate = DateTime.Parse("2026-06-16 12:00:01.3888343"),
                    UpdatedDate = DateTime.Parse("2026-06-16 12:00:01.3888346"),
                    IsDelete = false,
                    DisplayName = "PS-CC/EAC3-VN",
                    CostCenter = "00001839600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008665",
                    TargetResponsible = "51008662",
                    TrgtManagerLId = "33487021",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("0F251C9B-DEAC-44D8-B2FD-1142AD90736E"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213311"),
                    UpdatedDate = DateTime.Parse("2026-06-01 12:00:02.5210502"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.3-F1.1",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764282",
                    TargetResponsible = "50740822",
                    TrgtManagerLId = "33497118",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("F572A689-6762-42C1-AC69-117B99C34032"),
                    CreatedDate = DateTime.Parse("2026-05-04 12:00:01.8257608"),
                    UpdatedDate = DateTime.Parse("2026-05-04 12:00:01.8257612"),
                    IsDelete = false,
                    DisplayName = "HcP/TGA1",
                    CostCenter = "00001830180010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008150",
                    TargetResponsible = "51008149",
                    TrgtManagerLId = "33489582",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("C3C003F5-200E-452C-A0BC-118B8D3ABBB6"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213441"),
                    UpdatedDate = DateTime.Parse("2026-05-25 12:52:23.1930629"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.3",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50599026",
                    TargetResponsible = "50415391",
                    TrgtManagerLId = "33495628",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("54E0FFF0-DE3D-4C4B-8F48-11FCCC4FFED0"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213671"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:14:53.1887597"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS/QMM3-HcP",
                    CostCenter = "00001834400010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50407643",
                    TargetResponsible = "50151573",
                    TrgtManagerLId = "33508909",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("07EE2455-4EF7-426A-8EC4-1401E321E559"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212843"),
                    UpdatedDate = DateTime.Parse("2025-11-18 12:00:01.8590818"),
                    IsDelete = false,
                    DisplayName = "HcP/MFO2-LL06-LL07",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672596",
                    TargetResponsible = "50672588",
                    TrgtManagerLId = "33494166",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("F00B5866-D600-4482-9613-155FC3C3027C"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212676"),
                    UpdatedDate = DateTime.Parse("2026-06-03 10:20:29.5208326"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFE2.14",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50749579",
                    TargetResponsible = "50672623",
                    TrgtManagerLId = "33496592",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("4DCCD54F-76B6-4BC2-90E1-1734A39E506B"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212687"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545507"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFE3",
                    CostCenter = "00001838150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50730360",
                    TargetResponsible = "50407640",
                    TrgtManagerLId = "35709000",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("24F7CCD6-E8AF-4A25-B700-173E5F36E30F"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213270"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:15:05.8613687"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.1-F2.2",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50868305",
                    TargetResponsible = "50740820",
                    TrgtManagerLId = "33489109",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("10ED3316-0FFF-4A3A-A9B8-19B70BEAC52C"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212931"),
                    UpdatedDate = DateTime.Parse("2026-06-02 09:43:15.5933798"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-EL4-C",
                    CostCenter = "00001836500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50682678",
                    TargetResponsible = "50681530",
                    TrgtManagerLId = "33495263",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("74E56066-635C-4541-A301-1A19500C24D3"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213691"),
                    UpdatedDate = DateTime.Parse("2026-05-29 09:46:31.8032291"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS/QMM6.2-HcP",
                    CostCenter = "00001834100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50610489",
                    TargetResponsible = "50407645",
                    TrgtManagerLId = "33492685",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("7CE13D6D-C64D-4CFE-906E-1AFD9A646C1C"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212661"),
                    UpdatedDate = DateTime.Parse("2026-05-29 15:01:43.9027937"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFE2.11",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672624",
                    TargetResponsible = "50672623",
                    TrgtManagerLId = "33493899",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("28FEF20B-80FC-4730-A662-1BC465FECB7A"),
                    CreatedDate = DateTime.Parse("2025-11-02 12:00:02.2002940"),
                    UpdatedDate = DateTime.Parse("2026-06-01 10:23:49.9927752"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/ICO2",
                    CostCenter = "00001831290010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51004252",
                    TargetResponsible = "50373591",
                    TrgtManagerLId = "33492122",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("18ACB1DE-A5F6-4BC0-A412-1CA2662A1261"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213223"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:15:36.9500691"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.1-A12",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764373",
                    TargetResponsible = "50740820",
                    TrgtManagerLId = "33488235",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("1F67C178-D6F1-417B-BF74-1D56B9B86FAC"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213602"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0234956"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "PS/EVI-VN",
                    CostCenter = "00001839560010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50823608",
                    TargetResponsible = "10006651",
                    TrgtManagerLId = "35753443",
                    TypeOrganize = "A",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("2894F16C-89A5-4E1B-84C5-1DD61F56DCF7"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212484"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:15:40.4896681"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GR/FCM1-HcP",
                    CostCenter = "00001832000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50493562",
                    TargetResponsible = "50151442",
                    TrgtManagerLId = "33493666",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("2B7E9D00-9487-497F-A241-1EA7980DA5E9"),
                    CreatedDate = DateTime.Parse("2026-02-03 12:00:01.7603412"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:15:45.8118823"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO-TT",
                    CostCenter = "00001835000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51005749",
                    TargetResponsible = "50740817",
                    TrgtManagerLId = "33495496",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3AA39999-02D6-4CD2-B628-208A7974A031"),
                    CreatedDate = DateTime.Parse("2025-07-02 12:00:01.7082085"),
                    UpdatedDate = DateTime.Parse("2026-05-25 10:31:53.3449979"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.3.5",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51003050",
                    TargetResponsible = "50599026",
                    TrgtManagerLId = "33495414",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("668B65CC-13E4-4D4A-9000-21A20AAC5E3A"),
                    CreatedDate = DateTime.Parse("2026-06-16 12:00:01.3888350"),
                    UpdatedDate = DateTime.Parse("2026-06-16 12:00:01.3888353"),
                    IsDelete = false,
                    DisplayName = "PS-CC/EAC4-AS",
                    CostCenter = "00001839600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008666",
                    TargetResponsible = "51008662",
                    TrgtManagerLId = "33487165",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("5A2465B8-D3A5-4F49-81DB-21A2E11B7BC3"),
                    CreatedDate = DateTime.Parse("2026-06-02 12:00:01.1051219"),
                    UpdatedDate = DateTime.Parse("2026-06-05 14:24:16.0764323"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GS/OSD6-APAC2",
                    CostCenter = "C1820004070010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50166801",
                    TargetResponsible = "50974523",
                    TrgtManagerLId = "33496663",
                    TypeOrganize = "A",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("89A36C15-A10F-46EC-BEE0-230088FD788F"),
                    CreatedDate = DateTime.Parse("2026-05-04 12:00:01.8257626"),
                    UpdatedDate = DateTime.Parse("2026-05-04 12:00:01.8257629"),
                    IsDelete = false,
                    DisplayName = "HcP/TGA4",
                    CostCenter = "00001830180010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008153",
                    TargetResponsible = "51008149",
                    TrgtManagerLId = "33489029",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("436061A1-095E-4C9E-9FA1-24FCF7FCB81E"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213760"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0235336"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "PS-CT/ENG1-VN",
                    CostCenter = "00001839750010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50839593",
                    TargetResponsible = "50687774",
                    TrgtManagerLId = "33486987",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("F7A4D0D5-F073-45F9-9AEF-25A15949479A"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213147"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:15:54.8026880"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL09-D",
                    CostCenter = "00001835210010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50757047",
                    TargetResponsible = "50772824",
                    TrgtManagerLId = "33488459",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("F370C41A-82FE-4D32-A20F-28CFED1F7296"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212920"),
                    UpdatedDate = DateTime.Parse("2026-06-02 09:43:19.4388551"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-EL4-A",
                    CostCenter = "00001836500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50682676",
                    TargetResponsible = "50681530",
                    TrgtManagerLId = "33495272",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("4016EA61-CCD2-4826-905D-29DB709F5D5D"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213587"),
                    UpdatedDate = DateTime.Parse("2026-06-05 14:21:00.3975376"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "M/PUQ-HcP",
                    CostCenter = "00001831950010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50480520",
                    TargetResponsible = "50604437",
                    TrgtManagerLId = "33488672",
                    TypeOrganize = "G",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("18045F2D-6ACD-4BF2-A7BB-2A26005A0BD4"),
                    CreatedDate = DateTime.Parse("2026-06-16 12:00:01.3888364"),
                    UpdatedDate = DateTime.Parse("2026-06-16 12:00:01.3888367"),
                    IsDelete = false,
                    DisplayName = "PS-CC/ENG-VN",
                    CostCenter = "00001839600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008662",
                    TargetResponsible = "10005989",
                    TrgtManagerLId = "35398603",
                    TypeOrganize = "A",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("B8E87A51-4DAD-4004-803B-2A2C2F9737FB"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213466"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:16:09.3244205"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.4",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50694706",
                    TargetResponsible = "50415391",
                    TrgtManagerLId = "33489859",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("28FB9482-3960-4D69-99C5-2B9BBA0BF0AB"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213642"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:16:13.7944886"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS/QMM3.1-HcP",
                    CostCenter = "00001834400010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50610484",
                    TargetResponsible = "50407643",
                    TrgtManagerLId = "33493620",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("AD78B972-E009-47EF-B3DE-2BAF32347873"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212473"),
                    UpdatedDate = DateTime.Parse("2026-05-21 09:48:57.9857054"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GR/FCM1.4-HcP",
                    CostCenter = "00001832000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50796744",
                    TargetResponsible = "50493562",
                    TrgtManagerLId = "33495931",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("393FF60F-2215-4496-8C3E-2BFBAA662274"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213794"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0235377"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "PS-CT/ENG-VN",
                    CostCenter = "00001839750010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50687774",
                    TargetResponsible = "50013761",
                    TrgtManagerLId = "33486996",
                    TypeOrganize = "A",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("2B635763-5F77-4D44-A5D9-2DD5D494628C"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213681"),
                    UpdatedDate = DateTime.Parse("2026-05-29 09:46:36.6068963"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS/QMM6.1-HcP",
                    CostCenter = "00001834100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50610488",
                    TargetResponsible = "50407645",
                    TrgtManagerLId = "33491365",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("ED9A38D2-CF25-435E-9199-2FA147298AA5"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212671"),
                    UpdatedDate = DateTime.Parse("2026-05-29 15:01:49.9667184"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFE2.13",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50749578",
                    TargetResponsible = "50672623",
                    TrgtManagerLId = "33494219",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("A9F90B10-907F-40F7-9A54-320F7D7A3C06"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212636"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:16:37.5686396"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFE1.1",
                    CostCenter = "00001836500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50681523",
                    TargetResponsible = "50407638",
                    TrgtManagerLId = "33493121",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("DA7A7981-1D6E-4796-B70C-33B4F8AAD1E1"),
                    CreatedDate = DateTime.Parse("2026-07-01 12:00:01.1173524"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1173527"),
                    IsDelete = false,
                    DisplayName = "HcP/MSE4.1.1",
                    CostCenter = "00001833050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008952",
                    TargetResponsible = "51008948",
                    TrgtManagerLId = "33489065",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("40D09139-B6B1-4D4D-892E-34492A85B14C"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213361"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545651"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MSE3",
                    CostCenter = "00001838150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50407640",
                    TargetResponsible = "50114752",
                    TrgtManagerLId = "35709000",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("2AA89D2D-DE04-41AC-913D-36C5AB984D0F"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213662"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:16:51.7921017"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS/QMM3.2-D-HcP",
                    CostCenter = "00001834400010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50742967",
                    TargetResponsible = "50407643",
                    TrgtManagerLId = "33488137",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("961F289A-F940-44C3-990B-39567519CBEA"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213446"),
                    UpdatedDate = DateTime.Parse("2026-05-25 10:31:24.4633870"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.3.1",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50610496",
                    TargetResponsible = "50599026",
                    TrgtManagerLId = "33495478",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("CBB847C1-B841-4D4A-9924-3A6FD44EACC6"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212879"),
                    UpdatedDate = DateTime.Parse("2026-07-03 12:00:00.9549484"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO3",
                    CostCenter = "00001838150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50740817",
                    TargetResponsible = "50407640",
                    TrgtManagerLId = "35709000",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("DE8A0BAF-2873-4AF5-9212-3B0746319F0E"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212499"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:17:02.3720100"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GR/FCM4-HcP",
                    CostCenter = "00001832000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50494492",
                    TargetResponsible = "50151442",
                    TrgtManagerLId = "33494763",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D8A0E153-E9BF-4070-B838-3B9424CF6AF8"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213819"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:17:08.6184462"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS-CT/ENG3-VN",
                    CostCenter = "00001839750010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50831558",
                    TargetResponsible = "50687774",
                    TrgtManagerLId = "33487316",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("600D9013-0217-467B-A8A5-3BC5A0D5FC73"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212417"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:17:26.5868061"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "C/AUP13-VN",
                    CostCenter = "00001831010010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50485431",
                    TargetResponsible = "50907777",
                    TrgtManagerLId = "33495904",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("05EE7704-A054-415F-991D-3BDD23F6A44D"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213804"),
                    UpdatedDate = DateTime.Parse("2026-05-06 08:49:04.9212008"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS-CT/ETC2-VN",
                    CostCenter = "00001839510010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50610482",
                    TargetResponsible = "50410096",
                    TrgtManagerLId = "33492514",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("88EA5E04-5FB4-4CCA-B4E1-3CE964DF3FEC"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213123"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:17:36.1222458"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL07-B",
                    CostCenter = "00001835200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672602",
                    TargetResponsible = "50672596",
                    TrgtManagerLId = "33488510",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("CD5870E8-862A-4572-86CF-3D6BF150FAE7"),
                    CreatedDate = DateTime.Parse("2025-07-02 12:00:01.7082266"),
                    UpdatedDate = DateTime.Parse("2026-05-25 10:32:06.1960087"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.3.7",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51003052",
                    TargetResponsible = "50599026",
                    TrgtManagerLId = "33490179",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("E437F9C7-0BE4-46DE-AD12-3F8BB53D9337"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212813"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545592"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO1.2",
                    CostCenter = "00001836000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50939978",
                    TargetResponsible = "50681529",
                    TrgtManagerLId = "33496093",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3558EF33-86CC-41DC-BC50-4000B8DF5999"),
                    CreatedDate = DateTime.Parse("2025-11-11 12:00:01.7693431"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545637"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-TS-A",
                    CostCenter = "00001836100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51004572",
                    TargetResponsible = "50939978",
                    TrgtManagerLId = "33492630",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("EEFD2F54-FA82-4568-83DC-41118CBD260C"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213178"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:19:54.5630727"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL10-D",
                    CostCenter = "00001835210010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672616",
                    TargetResponsible = "50672612",
                    TrgtManagerLId = "33495682",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("93F46090-20A8-449E-A0B9-4311856CE895"),
                    CreatedDate = DateTime.Parse("2026-05-04 12:00:01.8257632"),
                    UpdatedDate = DateTime.Parse("2026-05-04 12:00:01.8257636"),
                    IsDelete = false,
                    DisplayName = "HcP/TGA5",
                    CostCenter = "00001830180010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008154",
                    TargetResponsible = "51008149",
                    TrgtManagerLId = "33494923",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("373E89EA-4FC9-4F13-8281-45DC85CC92C7"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213411"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545664"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3",
                    CostCenter = "00001833000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50415391",
                    TargetResponsible = "50151436",
                    TrgtManagerLId = "33492373",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("ABA9A60D-0BAF-4088-894E-5233B3E8A7D0"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212853"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:20:09.1515601"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO2-LL10-LL11",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672612",
                    TargetResponsible = "50672588",
                    TrgtManagerLId = "33488244",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("FD12BFEB-FB7C-4C86-9F9D-538A35F46F23"),
                    CreatedDate = DateTime.Parse("2026-01-03 12:00:01.5780666"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:20:12.7526813"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-CKD-C",
                    CostCenter = "00001835100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51005402",
                    TargetResponsible = "50672612",
                    TrgtManagerLId = "33496342",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("0DED0A86-F46D-4C7E-BD27-53CAAD6EA54B"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213371"),
                    UpdatedDate = DateTime.Parse("2026-05-25 11:35:36.3105080"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/PC",
                    CostCenter = "00001831000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50166305",
                    TargetResponsible = "10005986",
                    TrgtManagerLId = "35422426",
                    TypeOrganize = "L",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("DAE1094A-8C4A-4358-BF46-54FB1F56E649"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213627"),
                    UpdatedDate = DateTime.Parse("2025-07-01 12:00:01.3200588"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "PS/EPC-VN",
                    CostCenter = "00001839550010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50823610",
                    TargetResponsible = "50756758",
                    TrgtManagerLId = "35499166",
                    TypeOrganize = "A",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("61782C06-0CE7-4869-888D-5574CFD4CAF9"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212631"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:20:26.1492969"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/LOW-O",
                    CostCenter = "00001831600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50687787",
                    TargetResponsible = "50717328",
                    TrgtManagerLId = "33490749",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("B4307C20-1541-483E-BEEE-56F80E0C47B7"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212589"),
                    UpdatedDate = DateTime.Parse("2026-05-13 14:08:32.7621879"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/LOP",
                    CostCenter = "00001831300010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50716604",
                    TargetResponsible = "50151446",
                    TrgtManagerLId = "33491908",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("B31855A8-9AD5-491F-9AF7-577D493908F2"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213531"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:22:51.4444808"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF6",
                    CostCenter = "00001833150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50415392",
                    TargetResponsible = "50151436",
                    TrgtManagerLId = "33494950",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("EA5BA186-282E-4791-AF07-57DE985DEDEF"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213167"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:22:48.1660699"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL10-B",
                    CostCenter = "00001835210010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672614",
                    TargetResponsible = "50672612",
                    TrgtManagerLId = "33495637",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D01CF948-A9A0-4705-B3D3-59D7F441C993"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212682"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:22:45.1751801"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFE2.2",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672622",
                    TargetResponsible = "50407639",
                    TrgtManagerLId = "33496654",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("97D9F0A0-E6A4-4C77-836C-5C5CDF06EA71"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213237"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:22:55.8119711"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.1-F1.2",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764266",
                    TargetResponsible = "50740820",
                    TrgtManagerLId = "33490954",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("FF2A215B-723C-4583-8FCE-5FDA23A1BD6A"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212884"),
                    UpdatedDate = DateTime.Parse("2026-05-04 12:00:01.8920700"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO3.1",
                    CostCenter = "00001838150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50740820",
                    TargetResponsible = "50740817",
                    TrgtManagerLId = "33495575",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("1CFA5D80-0179-4BE8-B770-60B259D7009C"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213099"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:23:04.2618088"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL06-A",
                    CostCenter = "00001835200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50870544",
                    TargetResponsible = "50672596",
                    TrgtManagerLId = "33488636",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("95078D06-3713-4C02-846F-62AA8CB41F79"),
                    CreatedDate = DateTime.Parse("2025-12-02 12:00:01.2589747"),
                    UpdatedDate = DateTime.Parse("2026-02-02 09:23:29.5507581"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS/EDI7",
                    CostCenter = "00001839560010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51004859",
                    TargetResponsible = "50928435",
                    TrgtManagerLId = "35753443",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("18C4D855-85FA-469F-ADEF-636D9A740C45"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212610"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:23:10.3088077"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/LOW-A",
                    CostCenter = "00001831600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50687781",
                    TargetResponsible = "50717328",
                    TrgtManagerLId = "33496841",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("F72CD898-6727-4D17-9276-63BCB47D6B20"),
                    CreatedDate = DateTime.Parse("2026-05-04 12:00:01.8257622"),
                    UpdatedDate = DateTime.Parse("2026-05-04 12:00:01.8257626"),
                    IsDelete = false,
                    DisplayName = "HcP/TGA3",
                    CostCenter = "00001830180010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008152",
                    TargetResponsible = "51008149",
                    TrgtManagerLId = "33494157",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("CAAA2C5E-0676-48D2-B129-64C8FBBCFA68"),
                    CreatedDate = DateTime.Parse("2026-06-16 12:00:01.3888329"),
                    UpdatedDate = DateTime.Parse("2026-06-16 12:00:01.3888333"),
                    IsDelete = false,
                    DisplayName = "PS-CC/EAC1-VN",
                    CostCenter = "00001839600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008663",
                    TargetResponsible = "51008662",
                    TrgtManagerLId = "33487352",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3947B614-588E-453E-BD81-673EFEB4D820"),
                    CreatedDate = DateTime.Parse("2026-07-01 12:00:01.1173572"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1173575"),
                    IsDelete = false,
                    DisplayName = "HcP/MSE4.3.2",
                    CostCenter = "00001833050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008955",
                    TargetResponsible = "51008951",
                    TrgtManagerLId = "33497788",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("CEFA1414-49C9-4CDF-A777-678D423720B6"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213622"),
                    UpdatedDate = DateTime.Parse("2025-07-01 12:00:01.3200581"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "PS/EPC2-VN",
                    CostCenter = "00001839550010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50920748",
                    TargetResponsible = "50823610",
                    TrgtManagerLId = "35499166",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("88DEF0E0-A259-4D84-973F-680AA4929214"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213040"),
                    UpdatedDate = DateTime.Parse("2026-06-02 09:43:27.1932588"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-EL7-EOL-C",
                    CostCenter = "00001836100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50835270",
                    TargetResponsible = "50681532",
                    TrgtManagerLId = "33492603",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D951DF75-80A7-454C-9EBF-6A378D328AE2"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213789"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0235343"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "PS-CT/ENG2-VN",
                    CostCenter = "00001839750010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50839594",
                    TargetResponsible = "50687774",
                    TrgtManagerLId = "33487094",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("2610CA44-1E3E-4083-8010-6AECEDE80162"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212554"),
                    UpdatedDate = DateTime.Parse("2026-06-01 14:13:22.7180442"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/HRL2",
                    CostCenter = "00001830100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50407635",
                    TargetResponsible = "50151518",
                    TrgtManagerLId = "33491515",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3753D50F-D4A6-42E2-B06A-6C512EBD46E5"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212651"),
                    UpdatedDate = DateTime.Parse("2026-05-12 08:25:48.7900114"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFE1.2",
                    CostCenter = "00001836500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50784760",
                    TargetResponsible = "50407638",
                    TrgtManagerLId = "33491392",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("8F64162D-E802-4972-B28B-6C5710798BFE"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212605"),
                    UpdatedDate = DateTime.Parse("2026-06-01 12:00:02.5210488"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/LOW",
                    CostCenter = "00001831600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50717328",
                    TargetResponsible = "50151446",
                    TrgtManagerLId = "33488066",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("234B9E3E-B0F0-4305-A288-6CFF4816C3ED"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213104"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0234426"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL06-B",
                    CostCenter = "00001835200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672597",
                    TargetResponsible = "50672596",
                    TrgtManagerLId = "33496798",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("98B2CBE3-70FC-40D0-AA24-6D2A413DE208"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212569"),
                    UpdatedDate = DateTime.Parse("2026-06-05 14:43:03.6076044"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/HSE1",
                    CostCenter = "00001831020010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50494519",
                    TargetResponsible = "50151339",
                    TrgtManagerLId = "33495548",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("C683105A-85C4-42A9-9D21-6DE08657F07B"),
                    CreatedDate = DateTime.Parse("2025-11-02 12:00:02.2002283"),
                    UpdatedDate = DateTime.Parse("2026-06-01 09:27:34.6602439"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/ICO1",
                    CostCenter = "00001831270010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51004251",
                    TargetResponsible = "50373591",
                    TrgtManagerLId = "33490758",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("0170F28E-E522-4BF5-B018-6EC2068F59CC"),
                    CreatedDate = DateTime.Parse("2026-02-03 12:00:01.7603661"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:24:06.5381423"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO-TT2",
                    CostCenter = "00001835000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51005751",
                    TargetResponsible = "51005749",
                    TrgtManagerLId = "33497653",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3F2DABDB-07E4-4EAD-833C-71380E251E0E"),
                    CreatedDate = DateTime.Parse("2026-07-01 12:00:01.1173541"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1173548"),
                    IsDelete = false,
                    DisplayName = "HcP/MSE4.2",
                    CostCenter = "00001833050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008949",
                    TargetResponsible = "51008947",
                    TrgtManagerLId = "33496128",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("C49E615B-00D6-4DBE-B4C0-71AF747E396E"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212656"),
                    UpdatedDate = DateTime.Parse("2026-05-29 15:01:56.8897516"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFE2.1",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672623",
                    TargetResponsible = "50407639",
                    TrgtManagerLId = "33489537",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D07CEC0E-2313-411A-A83B-7253DF07BC7A"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212509"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:25:37.2987363"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GR/FCM-HcP",
                    CostCenter = "00001832000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50151442",
                    TargetResponsible = "50908876",
                    TrgtManagerLId = "33491677",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("A7913E32-3F78-4A20-88CB-72AC91F4AA4A"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213213"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:25:40.8535456"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-PL-C",
                    CostCenter = "00001835100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50772842",
                    TargetResponsible = "50772824",
                    TrgtManagerLId = "33494987",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("8CEA6204-FCED-4720-8C92-76439E5E01CA"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212818"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545599"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO1-EL7-EOL",
                    CostCenter = "00001838100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50681532",
                    TargetResponsible = "50681529",
                    TrgtManagerLId = "33494692",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("382E073B-7A99-442D-95B0-76F4E171F221"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213612"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0234970"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "PS/MFI-ITM",
                    CostCenter = "00002090080010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50934897",
                    TargetResponsible = "50713067",
                    TrgtManagerLId = "73164116",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("BB1A67A5-8A9C-455C-B3E5-77AEAB2106C8"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213597"),
                    UpdatedDate = DateTime.Parse("2026-02-02 12:00:01.5597033"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "PS/ENG-VN",
                    CostCenter = "00001839520010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50521185",
                    TargetResponsible = "50871365",
                    TrgtManagerLId = "36200976",
                    TypeOrganize = "A",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("E9EDDD3F-D187-46D6-960B-7878E37B7369"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213451"),
                    UpdatedDate = DateTime.Parse("2026-05-25 10:31:31.0017673"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.3.2",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50610498",
                    TargetResponsible = "50599026",
                    TrgtManagerLId = "33488690",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("35F77AB1-DF09-4580-9839-7A0016D55EC8"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212479"),
                    UpdatedDate = DateTime.Parse("2026-05-21 11:01:43.5873551"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GR/FCM1.5-HcP",
                    CostCenter = "00001832000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50796746",
                    TargetResponsible = "50493562",
                    TrgtManagerLId = "33490829",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("B714D41C-3509-4B88-951A-7D3E0986C7E4"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213814"),
                    UpdatedDate = DateTime.Parse("2026-06-01 12:00:02.5210505"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS-CT/ETC-VN",
                    CostCenter = "00001839500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50410096",
                    TargetResponsible = "50013761",
                    TrgtManagerLId = "33486996",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("37CD0E08-7544-4E65-9400-7DB0395F51C9"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213065"),
                    UpdatedDate = DateTime.Parse("2026-04-01 12:00:01.5761215"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-BACKUP-A",
                    CostCenter = "00001835200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50895449",
                    TargetResponsible = "50672596",
                    TrgtManagerLId = "33495708",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("7AC6D6CF-F792-4231-8B8F-7EB516C1AE11"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213537"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545671"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF6.1",
                    CostCenter = "00001833150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50736834",
                    TargetResponsible = "50415392",
                    TrgtManagerLId = "33494950",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("EDB00EC7-7B31-4208-8FB5-80D86DEBF3DA"),
                    CreatedDate = DateTime.Parse("2025-11-11 12:00:01.7693421"),
                    UpdatedDate = DateTime.Parse("2026-06-02 09:43:31.2108655"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-RW",
                    CostCenter = "00001836000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51004570",
                    TargetResponsible = "50939978",
                    TrgtManagerLId = "33491329",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("33FC5C6E-1274-4967-A817-810EEDF46246"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213291"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:26:25.4572897"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.2-F1.2",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764276",
                    TargetResponsible = "50740821",
                    TrgtManagerLId = "33492444",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("4F66FEF4-1EF1-4171-B9B8-81A954CE8714"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212462"),
                    UpdatedDate = DateTime.Parse("2026-05-21 09:48:48.4938551"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GR/FCM1.2-HcP",
                    CostCenter = "00001832000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50610501",
                    TargetResponsible = "50493562",
                    TrgtManagerLId = "33495307",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("FA4C5F13-6FE1-44B8-9A4F-821712CC443F"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212890"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:26:35.5201301"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO3.2",
                    CostCenter = "00001838150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50740821",
                    TargetResponsible = "50740817",
                    TrgtManagerLId = "33564036",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("6DAA7DE7-C82A-4771-9D22-82BA71FEEA4D"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213755"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0235329"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "PS-CC/PJM-C",
                    CostCenter = "00002136650010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50508795",
                    TargetResponsible = "50070094",
                    TrgtManagerLId = "70212034",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("1A74AE35-C17D-40FE-AB8E-836DA24747ED"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213632"),
                    UpdatedDate = DateTime.Parse("2026-05-29 14:01:44.0796946"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS/QMM1-HcP",
                    CostCenter = "00001834500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50407642",
                    TargetResponsible = "50151573",
                    TrgtManagerLId = "33488672",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("1AB29B91-37DA-4B8B-8B99-84B2F02F0C30"),
                    CreatedDate = DateTime.Parse("2026-01-03 12:00:01.5780659"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:26:46.8349477"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-CKD-B",
                    CostCenter = "00001835100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51005400",
                    TargetResponsible = "50672612",
                    TrgtManagerLId = "33496039",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("A49F8CBB-8EFB-4C8D-9866-84E2CD4FC512"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213336"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:26:54.9318814"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.3-F2.2",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764352",
                    TargetResponsible = "50740822",
                    TrgtManagerLId = "33488958",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("F16479C4-3043-44B8-A53A-8696D2CF12BF"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213281"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:26:58.9680734"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.2-F2.1",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764281",
                    TargetResponsible = "50740821",
                    TrgtManagerLId = "33493167",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("65A2599E-7D59-4FD2-8D67-86B0D2512FD0"),
                    CreatedDate = DateTime.Parse("2026-05-04 12:00:01.8257615"),
                    UpdatedDate = DateTime.Parse("2026-05-04 12:00:01.8257619"),
                    IsDelete = false,
                    DisplayName = "HcP/TGA2",
                    CostCenter = "00001830180010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008151",
                    TargetResponsible = "51008149",
                    TrgtManagerLId = "33494013",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("86990F8B-22F1-4850-855F-87D37582FA6B"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213809"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:27:02.2156257"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS-CT/ETC3-VN",
                    CostCenter = "00001839500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50610483",
                    TargetResponsible = "50410096",
                    TrgtManagerLId = "33489190",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D5BE466F-B08D-4618-8EE5-881E6E4F527D"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213128"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:27:06.0017777"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL07-C",
                    CostCenter = "00001835200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672603",
                    TargetResponsible = "50672596",
                    TrgtManagerLId = "33496574",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("E788E734-5B63-428B-B6B7-88C123EE0E48"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212544"),
                    UpdatedDate = DateTime.Parse("2026-06-01 14:13:30.9623864"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/HRL",
                    CostCenter = "00001830100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50151518",
                    TargetResponsible = "50166305",
                    TrgtManagerLId = "35794248",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("74FAA350-7B04-4BA2-9AE8-8A4A871EF708"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213242"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:27:12.7591868"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.1-F3",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764267",
                    TargetResponsible = "50740820",
                    TrgtManagerLId = "33489010",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("7DD35385-D7D6-4A7A-873D-8B2F7225AA79"),
                    CreatedDate = DateTime.Parse("2025-11-11 12:00:01.7693452"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545647"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-TS-C",
                    CostCenter = "00001836100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51004574",
                    TargetResponsible = "50939978",
                    TrgtManagerLId = "33495450",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("BEDCC092-0875-4F4F-9000-8BFB606C201C"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213227"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:27:23.1316350"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.1-F2.1",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764270",
                    TargetResponsible = "50740820",
                    TrgtManagerLId = "33489109",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("FEF5ABEC-6744-4019-916C-8C48AA462D78"),
                    CreatedDate = DateTime.Parse("2025-05-02 12:00:01.4060218"),
                    UpdatedDate = DateTime.Parse("2025-11-02 12:00:02.8100588"),
                    UpdatedBy = "5680A2EB-2E29-4AD9-ADD8-55E719B246E9",
                    IsDelete = false,
                    DisplayName = "M/LOI-EA",
                    CostCenter = "C1820004040010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51002117",
                    TargetResponsible = "50965394",
                    TrgtManagerLId = "33489181",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3200FD99-5010-41E0-9427-8CB29E7E348E"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213401"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545657"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF",
                    CostCenter = "00001833000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50151436",
                    TargetResponsible = "50114752",
                    TrgtManagerLId = "33489350",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("424D1CD9-C0F1-4E65-BF2E-8E08C0DBF58E"),
                    CreatedDate = DateTime.Parse("2024-08-02 12:00:02.1329683"),
                    UpdatedDate = DateTime.Parse("2025-06-03 13:03:44.2101166"),
                    UpdatedBy = "5680A2EB-2E29-4AD9-ADD8-55E719B246E9",
                    IsDelete = false,
                    DisplayName = "PS-CT/LOG1",
                    CostCenter = "00005681560010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50724286",
                    TargetResponsible = "50479719",
                    TrgtManagerLId = "32101369",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("8B639FEC-78A6-472D-B73E-8EC467C22305"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213321"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:27:36.9746075"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.3-F3",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764284",
                    TargetResponsible = "50740822",
                    TrgtManagerLId = "33495771",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("5A95099E-569A-427B-A2C7-8F1225B9C1F3"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212529"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:27:44.1817280"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/BPS",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50903250",
                    TargetResponsible = "50114752",
                    TrgtManagerLId = "35458520",
                    TypeOrganize = "A",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("C0218784-CF11-4EBF-8DC4-90797852E9EC"),
                    CreatedDate = DateTime.Parse("2026-02-03 12:00:01.7603668"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:27:58.3544685"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO-TT3",
                    CostCenter = "00001835000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51005752",
                    TargetResponsible = "51005749",
                    TrgtManagerLId = "33488011",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("59CDF779-57AB-4970-9C80-915F6119E2EC"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213396"),
                    UpdatedDate = DateTime.Parse("2026-05-21 13:16:27.9848805"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/PT",
                    CostCenter = "00001831000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50114752",
                    TargetResponsible = "50712622",
                    TrgtManagerLId = "35709000",
                    TypeOrganize = "L",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("7EFBEE7D-F32A-4E51-9FAE-92646EEE3BE2"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212539"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:28:07.8648327"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/CTG",
                    CostCenter = "00001831800010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50151654",
                    TargetResponsible = "50166305",
                    TrgtManagerLId = "34614944",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("AC1258B2-012B-4419-87E1-933B00EEB9E7"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213142"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:28:11.7518116"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL09-B",
                    CostCenter = "00001835210010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50757042",
                    TargetResponsible = "50772824",
                    TrgtManagerLId = "33490589",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("788B137A-786C-49B0-8C05-9879B818D322"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213208"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:28:21.1374035"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-PL-B",
                    CostCenter = "00001835100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50772837",
                    TargetResponsible = "50772824",
                    TrgtManagerLId = "33496814",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("A2231B1B-73B4-4F08-9B0B-98BBDC68CDD4"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213060"),
                    UpdatedDate = DateTime.Parse("2026-06-02 09:44:34.5270089"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-EL5-6-B",
                    CostCenter = "00001836500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50784766",
                    TargetResponsible = "50939978",
                    TrgtManagerLId = "33496510",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("53412261-E4E3-4554-BDE0-9E40BA7124AD"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212489"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:28:29.1662794"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GR/FCM2-HcP",
                    CostCenter = "00001832000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50493569",
                    TargetResponsible = "50151442",
                    TrgtManagerLId = "33488565",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("760F2F8F-301F-49D1-B8D5-9E5C4929979D"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212874"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:28:32.8643081"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO2-PL-LL09",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50772824",
                    TargetResponsible = "50672588",
                    TrgtManagerLId = "33493997",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3E72EEC8-1411-4395-AA02-A1ABF3E93EFD"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212895"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:28:39.3694164"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO3.3",
                    CostCenter = "00001838150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50740822",
                    TargetResponsible = "50740817",
                    TrgtManagerLId = "33495575",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("B8907AE6-8E2E-4F69-BE22-A2390AB2EC58"),
                    CreatedDate = DateTime.Parse("2026-07-01 12:00:01.1173561"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1173565"),
                    IsDelete = false,
                    DisplayName = "HcP/MSE4.3.1",
                    CostCenter = "00001833050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008954",
                    TargetResponsible = "51008951",
                    TrgtManagerLId = "33497350",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("8B897A87-D8DF-4F61-A25F-A4BFEFC3F4C8"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212559"),
                    UpdatedDate = DateTime.Parse("2026-06-01 14:13:39.6140635"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/HRL3",
                    CostCenter = "00001830100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50471364",
                    TargetResponsible = "50151518",
                    TrgtManagerLId = "33493951",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("7487B934-F6F9-4B5E-A81D-A6066DFF057A"),
                    CreatedDate = DateTime.Parse("2024-08-01 12:00:00.9083716"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:28:47.9290352"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.2-F2.2",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50974901",
                    TargetResponsible = "50740821",
                    TrgtManagerLId = "33493167",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("F9CFE1F2-1E03-42CF-90BE-A7426886A43C"),
                    CreatedDate = DateTime.Parse("2026-06-16 12:00:01.3888357"),
                    UpdatedDate = DateTime.Parse("2026-06-16 12:00:01.3888360"),
                    IsDelete = false,
                    DisplayName = "PS-CC/EAD-VN",
                    CostCenter = "00001839600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008667",
                    TargetResponsible = "51008662",
                    TrgtManagerLId = "33487174",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("A99996C6-A0FF-4CD0-9A22-A8096D52F3D8"),
                    CreatedDate = DateTime.Parse("2026-06-16 12:00:01.3888336"),
                    UpdatedDate = DateTime.Parse("2026-06-16 12:00:01.3888340"),
                    IsDelete = false,
                    DisplayName = "PS-CC/EAC2-VN",
                    CostCenter = "00001839600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008664",
                    TargetResponsible = "51008662",
                    TrgtManagerLId = "33560995",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("029F55F0-D205-4F57-9491-A8B93600DC94"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212179"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0233352"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "2WP/PJM-AS",
                    CostCenter = "00001820010010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50950219",
                    TargetResponsible = "50760184",
                    TrgtManagerLId = "35910452",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("8B8BC59D-9F4A-4787-8CB3-AC4ACC10BDC5"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212564"),
                    UpdatedDate = DateTime.Parse("2026-06-05 14:43:14.0098108"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/HSE",
                    CostCenter = "00001831020010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50151339",
                    TargetResponsible = "50114752",
                    TrgtManagerLId = "33492168",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("7BA1498B-93F6-43E5-8D6C-AE2C9290AA4C"),
                    CreatedDate = DateTime.Parse("2026-07-01 12:00:01.1173534"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1173538"),
                    IsDelete = false,
                    DisplayName = "HcP/MSE4.1.2",
                    CostCenter = "00001833050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008953",
                    TargetResponsible = "51008948",
                    TrgtManagerLId = "33508865",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D113FEB5-166B-46A9-9D44-AE5D44699AA2"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213276"),
                    UpdatedDate = DateTime.Parse("2026-03-17 14:29:03.1184911"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.2-A12",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764279",
                    TargetResponsible = "50740821",
                    TrgtManagerLId = "33494068",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("EEBE376D-DEE4-45BC-B641-AFE4633A7D74"),
                    CreatedDate = DateTime.Parse("2025-05-02 12:00:01.4059718"),
                    UpdatedDate = DateTime.Parse("2025-11-02 12:00:02.8100564"),
                    IsDelete = false,
                    DisplayName = "HcP/LOW-D",
                    CostCenter = "00001831600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51002275",
                    TargetResponsible = "50717328",
                    TrgtManagerLId = "33489706",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("763920C2-43AE-4FD9-AB5C-B490A104D140"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213542"),
                    UpdatedDate = DateTime.Parse("2026-06-16 08:35:43.1942545"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF6.2",
                    CostCenter = "00001833150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50747321",
                    TargetResponsible = "50415392",
                    TrgtManagerLId = "33496477",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("89E6CAA5-3AE3-4972-9176-B51F9BDF51A3"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213301"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0234631"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.3-A12",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764286",
                    TargetResponsible = "50740822",
                    TrgtManagerLId = "33491310",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("A6C5BD5A-2920-40E7-960C-B5241781456A"),
                    CreatedDate = DateTime.Parse("2026-05-04 12:00:01.8257639"),
                    UpdatedDate = DateTime.Parse("2026-05-04 12:00:01.8257643"),
                    IsDelete = false,
                    DisplayName = "HcP/TGA6",
                    CostCenter = "00001830180010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008155",
                    TargetResponsible = "51008149",
                    TrgtManagerLId = "33490598",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D7C278FC-90C4-4887-9AE1-B60686173EB0"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212692"),
                    UpdatedDate = DateTime.Parse("2026-05-01 12:00:01.1409553"),
                    IsDelete = false,
                    DisplayName = "HcP/MFE3.1",
                    CostCenter = "00001838150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50932251",
                    TargetResponsible = "50730360",
                    TrgtManagerLId = "33493871",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("AB6070FD-ABF1-40FD-9305-B656525A134A"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213203"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0234525"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-PL-A",
                    CostCenter = "00001835100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50772831",
                    TargetResponsible = "50772824",
                    TrgtManagerLId = "33497635",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("9D33E380-B8C7-4AED-AFAF-B93F00FB54B3"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212514"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0233488"),
                    IsDelete = false,
                    DisplayName = "GR/SES-HcP",
                    CostCenter = "00001831040010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50619140",
                    TargetResponsible = "50151442",
                    TrgtManagerLId = "33497163",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("1F52D895-BDC3-40D3-830E-B95AD1A0E59D"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212727"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545572"),
                    IsDelete = false,
                    DisplayName = "HcP/MFE3.2",
                    CostCenter = "00001838150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50932252",
                    TargetResponsible = "50730360",
                    TrgtManagerLId = "35709000",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("FEE41A03-EC0E-4458-9AC1-B991E57E7FF6"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213035"),
                    UpdatedDate = DateTime.Parse("2026-06-02 09:44:38.9170857"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-EL5-6-C",
                    CostCenter = "00001836500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50835013",
                    TargetResponsible = "50939978",
                    TrgtManagerLId = "33496020",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("86713DA6-438E-409D-9808-BA0BAED7960A"),
                    CreatedDate = DateTime.Parse("2026-07-01 12:00:01.1173551"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1173558"),
                    IsDelete = false,
                    DisplayName = "HcP/MSE4.3",
                    CostCenter = "00001833050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008951",
                    TargetResponsible = "51008947",
                    TrgtManagerLId = "33497788",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("1D6D05BD-90DA-4813-B5BA-BB05B47FCE85"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213617"),
                    UpdatedDate = DateTime.Parse("2025-07-01 12:00:01.3200571"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "PS/EPC1-VN",
                    CostCenter = "00001839550010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50920747",
                    TargetResponsible = "50823610",
                    TrgtManagerLId = "33835048",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("B2983B3B-222C-44C3-AD5E-BBB962CDBCBC"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212504"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0233478"),
                    UpdatedBy = "92F63858-8F42-4D90-B410-6999B96A829A",
                    IsDelete = false,
                    DisplayName = "GR/FCM-AS2",
                    CostCenter = "00001832000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50908876",
                    TargetResponsible = "50244556",
                    TrgtManagerLId = "33491677",
                    TypeOrganize = "A",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("1D66A9F0-4F60-445F-A418-BD2107C7543F"),
                    CreatedDate = DateTime.Parse("2026-05-04 12:00:01.8257369"),
                    UpdatedDate = DateTime.Parse("2026-05-04 12:00:01.8257595"),
                    IsDelete = false,
                    DisplayName = "HcP/TGA",
                    CostCenter = "00001830180010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008149",
                    TargetResponsible = "50151518",
                    TrgtManagerLId = "33490099",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("F203362F-16B8-48DA-AF0C-C03C0470F935"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212579"),
                    UpdatedDate = DateTime.Parse("2026-05-25 11:48:21.3076131"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/LOG",
                    CostCenter = "00001831300010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50151446",
                    TargetResponsible = "50166305",
                    TrgtManagerLId = "33488057",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3691D9B5-A4A7-45F7-B974-C19BE926B0B7"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213232"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0234556"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.1-F1.1",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764263",
                    TargetResponsible = "50740820",
                    TrgtManagerLId = "33488048",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D3D8F3A5-8DC0-48A8-96F6-C1C57AD6EFE7"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213461"),
                    UpdatedDate = DateTime.Parse("2026-05-25 10:31:46.9711339"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.3.4",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50694723",
                    TargetResponsible = "50599026",
                    TrgtManagerLId = "33494120",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("5E51AEED-FA41-4AA6-B114-C62FE0496985"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212926"),
                    UpdatedDate = DateTime.Parse("2026-06-02 09:44:42.7436460"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-EL4-B",
                    CostCenter = "00001836500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50682677",
                    TargetResponsible = "50681530",
                    TrgtManagerLId = "33495959",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("6527DB9C-54D0-4B07-A10D-C7B42B91DD46"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213657"),
                    UpdatedDate = DateTime.Parse("2026-02-18 12:00:01.1820493"),
                    IsDelete = false,
                    DisplayName = "PS/QMM3.2-C-HcP",
                    CostCenter = "00001834400010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50742966",
                    TargetResponsible = "50407643",
                    TrgtManagerLId = "33489519",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("C0C3BAFE-C80D-4AE6-BC69-C8C6D58C2942"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212833"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0233762"),
                    IsDelete = false,
                    DisplayName = "HcP/MFO2",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672588",
                    TargetResponsible = "50407639",
                    TrgtManagerLId = "33488093",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("63ADB552-DB4D-4282-8DF3-CA992968DDB3"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212808"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545586"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO1.1",
                    CostCenter = "00001836100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50681530",
                    TargetResponsible = "50681529",
                    TrgtManagerLId = "33494585",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("CFFE961C-B891-430D-BF17-CB3FAF55D965"),
                    CreatedDate = DateTime.Parse("2026-07-01 12:00:01.1173206"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1173490"),
                    IsDelete = false,
                    DisplayName = "HcP/MSE4",
                    CostCenter = "00001833050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008947",
                    TargetResponsible = "50114752",
                    TrgtManagerLId = "33497788",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("0AFA1F1B-DBEA-45A1-8A8D-CB5EDADDA615"),
                    CreatedDate = DateTime.Parse("2025-01-03 12:00:01.4765977"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0233605"),
                    IsDelete = false,
                    DisplayName = "HcP/LOW-C",
                    CostCenter = "00001831600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50985253",
                    TargetResponsible = "50717328",
                    TrgtManagerLId = "33492202",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("8133DC26-281D-435A-8757-CF1B95E764F3"),
                    CreatedDate = DateTime.Parse("2025-07-02 12:00:01.7082277"),
                    UpdatedDate = DateTime.Parse("2026-05-25 10:31:16.5393736"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.3.8",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51003053",
                    TargetResponsible = "50599026",
                    TrgtManagerLId = "33490311",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("73545688-99B0-47BE-B306-CFF9A13873F7"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213356"),
                    UpdatedDate = DateTime.Parse("2025-10-27 14:05:13.8007325"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MSE2",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50407639",
                    TargetResponsible = "50114752",
                    TrgtManagerLId = "35458520",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D0F25674-125E-448D-8F22-D21106BDC721"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213316"),
                    UpdatedDate = DateTime.Parse("2025-11-06 12:00:01.7121689"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.3-F1.2",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764283",
                    TargetResponsible = "50740822",
                    TrgtManagerLId = "33494932",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("C0B65BFC-0C41-45BD-B0DD-D347F5763DB0"),
                    CreatedDate = DateTime.Parse("2026-06-01 12:00:02.5003373"),
                    UpdatedDate = DateTime.Parse("2026-06-01 12:00:02.5003472"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.2-R13",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008547",
                    TargetResponsible = "50740821",
                    TrgtManagerLId = "33488422",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("F4262A3F-0D88-4F4B-A783-D3BD5712CEA7"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213456"),
                    UpdatedDate = DateTime.Parse("2026-05-25 10:31:38.8356498"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.3.3",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50610499",
                    TargetResponsible = "50599026",
                    TrgtManagerLId = "33490525",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("C639A727-BDB8-4FEF-89B7-D5076CD4ED14"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213029"),
                    UpdatedDate = DateTime.Parse("2026-06-02 09:44:47.0654446"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-EL7-EOL-B",
                    CostCenter = "00001836100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50835012",
                    TargetResponsible = "50681532",
                    TrgtManagerLId = "33491668",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("37063120-7C42-462B-A905-D53195598235"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213351"),
                    UpdatedDate = DateTime.Parse("2025-10-27 14:04:54.9589602"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MSE1",
                    CostCenter = "00001836500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50407638",
                    TargetResponsible = "50114752",
                    TrgtManagerLId = "33494692",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("9DB3FA56-CAC4-4A15-891C-D66572EC43C6"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212646"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0233632"),
                    IsDelete = false,
                    DisplayName = "HcP/MFE1.12",
                    CostCenter = "00001836000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50681526",
                    TargetResponsible = "50681523",
                    TrgtManagerLId = "33489840",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("62616A75-6ADD-4F80-AB13-D70C03163449"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212584"),
                    UpdatedDate = DateTime.Parse("2026-06-01 12:00:02.5210481"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/LOM",
                    CostCenter = "00001831600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50867996",
                    TargetResponsible = "50717328",
                    TrgtManagerLId = "33488066",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("FD93C92F-4071-499E-872D-D753CDCEAFDE"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212641"),
                    UpdatedDate = DateTime.Parse("2026-06-01 12:00:02.5210495"),
                    IsDelete = false,
                    DisplayName = "HcP/MFE1.11",
                    CostCenter = "00001836000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50681525",
                    TargetResponsible = "50681523",
                    TrgtManagerLId = "33496887",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("03805576-8DDA-4AD0-87EB-DA734CEF9AF8"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212468"),
                    UpdatedDate = DateTime.Parse("2026-05-21 09:48:44.2663125"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GR/FCM1.3-HcP",
                    CostCenter = "00001832000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50739708",
                    TargetResponsible = "50493562",
                    TrgtManagerLId = "33488985",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("9E10DD9C-48D2-463A-886A-DB596EE58361"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213137"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0234464"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL09-A",
                    CostCenter = "00001835210010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50757041",
                    TargetResponsible = "50772824",
                    TrgtManagerLId = "33493504",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("F76ED496-8792-44FC-A0ED-DBE7D19676ED"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212494"),
                    UpdatedDate = DateTime.Parse("2026-05-29 15:56:59.7321344"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GR/FCM3-HcP",
                    CostCenter = "00001832000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50517821",
                    TargetResponsible = "50151442",
                    TrgtManagerLId = "33495290",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("B632B408-B8D4-4129-8BE9-DDBEFB188156"),
                    CreatedDate = DateTime.Parse("2026-02-03 12:00:01.7603651"),
                    UpdatedDate = DateTime.Parse("2026-02-03 12:00:01.7603658"),
                    IsDelete = false,
                    DisplayName = "HcP/MFO-TT1",
                    CostCenter = "00001835000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51005750",
                    TargetResponsible = "51005749",
                    TrgtManagerLId = "33488486",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("0D19D437-D182-4703-8917-DE950DD7B889"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213024"),
                    UpdatedDate = DateTime.Parse("2026-01-03 12:00:01.6528089"),
                    UpdatedBy = "5680A2EB-2E29-4AD9-ADD8-55E719B246E9",
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-EL7-EOL-A",
                    CostCenter = "00001836100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50835011",
                    TargetResponsible = "50681532",
                    TrgtManagerLId = "33491285",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("6618C035-0B27-4F7D-831C-DEB04969ED69"),
                    CreatedDate = DateTime.Parse("2026-01-03 12:00:01.5780529"),
                    UpdatedDate = DateTime.Parse("2026-01-03 12:00:01.5780649"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-CKD-A",
                    CostCenter = "00001835050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51005399",
                    TargetResponsible = "50672612",
                    TrgtManagerLId = "33497190",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("18572CC1-4F01-4BA6-8F33-E05E50CBA009"),
                    CreatedDate = DateTime.Parse("2026-05-01 12:00:01.0919216"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545579"),
                    IsDelete = false,
                    DisplayName = "HcP/MFE3-NBD",
                    CostCenter = "00001838150010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008145",
                    TargetResponsible = "50407640",
                    TrgtManagerLId = "35709000",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3D06AF94-50D3-477E-86A8-E0D2931EAFF7"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212410"),
                    UpdatedDate = DateTime.Parse("2026-04-17 12:00:00.9181324"),
                    UpdatedBy = "A4B76024-BF17-494F-B064-A09BA2E10E42",
                    IsDelete = false,
                    DisplayName = "BD/SLP-AOK3",
                    CostCenter = "C1820004100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50631292",
                    TargetResponsible = "50077581",
                    TrgtManagerLId = "35713781",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("BD44BDD6-6A56-48AA-900D-E226BBA75690"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213406"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545661"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF1",
                    CostCenter = "00001833000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50645295",
                    TargetResponsible = "50151436",
                    TrgtManagerLId = "33489350",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("839E4072-9DE8-4A93-A35E-E2502EF3C039"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213416"),
                    UpdatedDate = DateTime.Parse("2025-11-20 12:00:02.0371808"),
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.1",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50599019",
                    TargetResponsible = "50415391",
                    TrgtManagerLId = "33490678",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("A7AA9D87-9AB9-457C-9DAE-E254B5182263"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213117"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0234440"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL07-A",
                    CostCenter = "00001835200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672601",
                    TargetResponsible = "50672596",
                    TrgtManagerLId = "33496805",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3A355F1F-4ACA-413D-9E7A-E2B17D21341F"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212433"),
                    UpdatedDate = DateTime.Parse("2026-05-21 09:48:39.8239537"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "GR/FCM1.1-HcP",
                    CostCenter = "00001832000010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50610500",
                    TargetResponsible = "50493562",
                    TrgtManagerLId = "33492140",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("BF79B045-582F-4499-9C9F-E2C458FCC614"),
                    CreatedDate = DateTime.Parse("2024-12-29 12:00:01.3349559"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0233201"),
                    UpdatedBy = "92F63858-8F42-4D90-B410-6999B96A829A",
                    IsDelete = false,
                    DisplayName = "2WP/ENG-AS",
                    CostCenter = "00001839760010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50969581",
                    TargetResponsible = "50760184",
                    TrgtManagerLId = "35708074",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("9B0130BF-B2B5-4B3F-A083-E63BC1BC2ACE"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212936"),
                    UpdatedDate = DateTime.Parse("2026-01-24 12:00:01.9249780"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-EL5-6-A",
                    CostCenter = "00001836500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50682679",
                    TargetResponsible = "50939978",
                    TrgtManagerLId = "33495183",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("E3920B29-8362-4694-BDD5-E68BA6BBE59C"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213286"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0234597"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW3.2-F1.1",
                    CostCenter = "00001838200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50764271",
                    TargetResponsible = "50740821",
                    TrgtManagerLId = "33495986",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("FF507DF2-B223-49A6-8FFE-E7D3CF374D78"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213173"),
                    UpdatedDate = DateTime.Parse("2025-07-23 12:00:01.2423433"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL10-C",
                    CostCenter = "00001835210010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672615",
                    TargetResponsible = "50672612",
                    TrgtManagerLId = "33491356",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("C0216C62-95AC-4A04-B109-E82195F1C7A4"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213799"),
                    UpdatedDate = DateTime.Parse("2026-05-20 14:16:17.3731841"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS-CT/ETC1-VN",
                    CostCenter = "00001839510010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50737079",
                    TargetResponsible = "50410096",
                    TrgtManagerLId = "33488128",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("3C8CB8D0-EC82-4653-93DA-EAB11A6AA8AC"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212768"),
                    UpdatedDate = DateTime.Parse("2026-07-03 12:00:00.9549351"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/MFO1",
                    CostCenter = "00001836500010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50681529",
                    TargetResponsible = "50407638",
                    TrgtManagerLId = "33495913",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D3FE84FC-4483-4EFC-972A-F152E41656CC"),
                    CreatedDate = DateTime.Parse("2026-07-01 12:00:01.1173514"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1173517"),
                    IsDelete = false,
                    DisplayName = "HcP/MSE4.1",
                    CostCenter = "00001833050010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51008948",
                    TargetResponsible = "51008947",
                    TrgtManagerLId = "33508865",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("D7BBC116-BAC0-482E-82EE-F49B8F9A70FD"),
                    CreatedDate = DateTime.Parse("2025-07-02 12:00:01.7082256"),
                    UpdatedDate = DateTime.Parse("2026-05-25 10:31:59.6439958"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/TEF3.3.6",
                    CostCenter = "00001833100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51003051",
                    TargetResponsible = "50599026",
                    TrgtManagerLId = "33492453",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("543DC5DA-47ED-4112-87A5-F86EC7D06BAB"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212574"),
                    UpdatedDate = DateTime.Parse("2026-06-01 09:27:40.4891209"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/ICO",
                    CostCenter = "00001833200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50373591",
                    TargetResponsible = "50166305",
                    TrgtManagerLId = "33492738",
                    TypeOrganize = "A",
                    IsDepartment = true
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("0DD6CCCF-956C-4D41-84B0-F9553CA712DD"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3212615"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0233598"),
                    IsDelete = false,
                    DisplayName = "HcP/LOW-B",
                    CostCenter = "00001831600010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50687784",
                    TargetResponsible = "50717328",
                    TrgtManagerLId = "33496066",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("92857C7A-8E02-4A97-888F-F97CC340FE44"),
                    CreatedDate = DateTime.Parse("2025-11-02 12:00:02.2002950"),
                    UpdatedDate = DateTime.Parse("2026-06-02 10:18:43.9804437"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "HcP/ICO3",
                    CostCenter = "00001833200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51004253",
                    TargetResponsible = "50373591",
                    TrgtManagerLId = "33494870",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("93C9897B-5EB1-4048-8D34-FCE05D5281BB"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213109"),
                    UpdatedDate = DateTime.Parse("2025-04-11 12:00:01.0234433"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW2-LL06-C",
                    CostCenter = "00001835200010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50672598",
                    TargetResponsible = "50672596",
                    TrgtManagerLId = "33496896",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("0C64DD19-6687-4B0C-8703-FCE17B203099"),
                    CreatedDate = DateTime.Parse("2023-12-05 11:15:09.3213706"),
                    UpdatedDate = DateTime.Parse("2026-05-29 09:46:41.4844920"),
                    UpdatedBy = "993F9106-79CB-4BB0-B061-EBDE116C32B6",
                    IsDelete = false,
                    DisplayName = "PS/QMM6-HcP",
                    CostCenter = "00001834100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "50407645",
                    TargetResponsible = "50151573",
                    TrgtManagerLId = "33491365",
                    TypeOrganize = "G",
                    IsDepartment = false
                },
                                new OrgUnit
                {
                    Id = Guid.Parse("AD7F3CED-AAF9-47A4-8E9B-FE776E583A6A"),
                    CreatedDate = DateTime.Parse("2025-11-11 12:00:01.7693442"),
                    UpdatedDate = DateTime.Parse("2026-07-01 12:00:01.1545644"),
                    IsDelete = false,
                    DisplayName = "HcP/MFW1-TS-B",
                    CostCenter = "00001836100010",
                    DiscManagerLId = "00000000",
                    OrgUnitCode = "51004573",
                    TargetResponsible = "50939978",
                    TrgtManagerLId = "33496967",
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
