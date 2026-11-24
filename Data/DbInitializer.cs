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
        await SeedRolePermissionsAsync(db);
        await SeedEvalScenariosAsync(db);

        var discovery = scope.ServiceProvider.GetRequiredService<ToolDiscoveryService>();
        var newTools = await discovery.SyncToolDefinitionsAsync();

        if (!await db.AiModels.AnyAsync())
        {
            db.AiModels.AddRange(
                new AiModel { ModelId = "qwen3.6-27b@q3_k_s", Endpoint = "http://127.0.0.1:1234/v1", ApiKey = "lm-studio", SupportsVision = false, ContextWindow = 128000 },
                // DeepSeek nhận json_object nhưng 400 với json_schema; OpenAI nhận cả hai — seed đúng mức mà
                // mỗi endpoint thật sự chấp nhận, model local để None cho an toàn.
                new AiModel { ModelId = "deepseek-v4-flash", Endpoint = "https://api.deepseek.com", ApiKey = "", SupportsVision = false, ContextWindow = 1000000, InputPricePerMillionTokens = 0.14m, CachedInputPricePerMillionTokens = 0.014m, OutputPricePerMillionTokens = 0.28m, StructuredOutputMode = StructuredOutputMode.JsonObject },
                new AiModel { ModelId = "gpt-5-nano", Endpoint = "https://api.openai.com/v1", ApiKey = "", SupportsVision = true, ContextWindow = 400000, InputPricePerMillionTokens = 0.05m, CachedInputPricePerMillionTokens = 0.005m, OutputPricePerMillionTokens = 0.4m, StructuredOutputMode = StructuredOutputMode.JsonSchema },
                // Model MẶC ĐỊNH của app (xem thứ tự ưu tiên khi gắn agent bên dưới).
                // CachedInputPricePerMillionTokens PHẢI khai đúng 0.02: để 0 nghĩa là "chưa khai báo" chứ
                // không phải miễn phí, và khi đó trang Usage tính mọi token đọc từ cache theo giá input đầy
                // đủ — báo cáo sẽ giấu đi đúng khoản mà prompt cache vừa tiết kiệm được (xem AiModel).
                // ContextWindow 1.050.000 là giới hạn KỸ THUẬT; giới hạn KINH TẾ thấp hơn nhiều và nằm ở
                // PromptBudget (vách giá 272K token).
                new AiModel { ModelId = "gpt-5.6-luna", Endpoint = "https://api.openai.com/v1", ApiKey = "", SupportsVision = true, ContextWindow = 1050000, InputPricePerMillionTokens = 0.2m, CachedInputPricePerMillionTokens = 0.02m, OutputPricePerMillionTokens = 1.2m, StructuredOutputMode = StructuredOutputMode.JsonSchema }
            );
            await db.SaveChangesAsync();
        }

        if (!await db.Agents.AnyAsync())
        {
            var modelId = await db.AiModels
                .OrderByDescending(x => x.ModelId == "gpt-5.6-luna")
                .ThenBy(x => x.ModelId)
                .Select(x => x.Id)
                .FirstAsync();
            var agents = new[]
            {
                new Agent { RoleKey=AgentRoleKey.BusinessAnalyst, Color="#8B5CF6", AiModelId=modelId, Description="Gathers and analyzes requirements, writes business specification documents." },
                new Agent { RoleKey=AgentRoleKey.TechLead, Color="#3B82F6", AiModelId=modelId, Description="Thiết kế kiến trúc và review kỹ thuật." },
                new Agent { RoleKey=AgentRoleKey.Developer, Color="#10B981", AiModelId=modelId, Description="Sinh source code, build và sửa lỗi." },
                new Agent { RoleKey=AgentRoleKey.Tester, Color="#2563EB", AiModelId=modelId, Description="Viết test cases và kiểm thử." },
                new Agent { RoleKey=AgentRoleKey.UiUx, Color="#F97316", AiModelId=modelId, Description="Thiết kế flow và wireframe." }
            };
            db.Agents.AddRange(agents);
            await db.SaveChangesAsync();

            await AssignDefaultToolsAsync(db);
        }

        // DB đã có agent từ trước: tool MỚI thêm vào code phải được cấp bù, nếu không nó chỉ tồn tại
        // trên máy cài mới. Chỉ áp cho tool lần đầu xuất hiện — xem GrantNewToolsAsync.
        await GrantNewToolsAsync(db, newTools);
    }

    // Quyền mặc định khi bảng RolePermission còn trống: CHỈ role Admin (RoleNames.Admin), với TOÀN BỘ quyền
    // (chỉnh được sau). Mọi role IS4 khác bắt đầu không có quyền nào — Admin tự cấp ở popup Permissions.
    private static async Task SeedRolePermissionsAsync(AppDbContext db)
    {
        if (await db.RolePermissions.AnyAsync())
            return;

        foreach (var permission in PermissionCatalog.AllPermissions)
            db.RolePermissions.Add(new RolePermission { Role = RoleNames.Admin, Permission = permission });

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

    // Golden set mặc định cho Prompt Evals (xem EvalScenariosSeedData) — chỉ seed khi bảng còn trống,
    // để bộ scenario người dùng đã chỉnh/tắt không bị ghi đè ở các lần khởi động sau.
    private static async Task SeedEvalScenariosAsync(AppDbContext db)
    {
        if (await db.EvalScenarios.AnyAsync())
            return;

        db.EvalScenarios.AddRange(EvalScenariosSeedData.Build());
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Tập tool mặc định của mỗi vai. Là nguồn DUY NHẤT cho cả hai đường cấp tool: lần seed đầu
    /// (<see cref="AssignDefaultToolsAsync"/>) và lần cấp bù khi một tool MỚI xuất hiện trên DB đã có
    /// sẵn (<see cref="GrantNewToolsAsync"/>). Hai bảng riêng thì tool mới chỉ tới được máy cài mới.
    /// </summary>
    private static readonly IReadOnlyDictionary<AgentRoleKey, string[]> DefaultToolsByRole =
        new Dictionary<AgentRoleKey, string[]>
        {
            [AgentRoleKey.BusinessAnalyst] = ["ListFiles", "ReadFile", "WriteFile", "SearchFiles", "SearchInFiles"],
            [AgentRoleKey.TechLead] = ["ListFiles", "ReadFile", "WriteFile", "SearchFiles", "SearchInFiles", "GitDiff", "GitStatus"],
            // SearchInFiles là tool ĐIỀU HƯỚNG của vai này: các bước Implementation/BugFix/BuildFix phải
            // tìm chỗ cần sửa trong một cây code có sẵn (skeleton Bosch, hoặc chính code vòng trước), mà
            // SearchFiles chỉ khớp ĐƯỜNG DẪN nên không trả lời được "chỗ nào đang dùng cái này".
            [AgentRoleKey.Developer] =
            [
                "ListFiles", "ReadFile", "WriteFile", "WriteFiles", "ReplaceInFile", "SearchFiles", "SearchInFiles",
                "SetPocContent", "AppendPocContent", "SetPocScript", "AppendPocScript", "AuditPocContent", "FixPocAndAudit",
                "RunCommand", "GitStatus", "GitCommit", "CreateBranch", "PushBranch", "OpenPullRequest"
            ],
            [AgentRoleKey.Tester] = ["ListFiles", "ReadFile", "WriteFile", "SearchFiles", "SearchInFiles", "RunCommand"],
            [AgentRoleKey.UiUx] = ["WriteFile", "ReadFile", "ListFiles"]
        };

    private static async Task AssignDefaultToolsAsync(AppDbContext db)
    {
        var all = await db.ToolDefinitions.ToListAsync();
        foreach (var (roleKey, toolNames) in DefaultToolsByRole)
        {
            var agent = await db.Agents.FirstOrDefaultAsync(x => x.RoleKey == roleKey);
            if (agent == null)
                continue;
            foreach (var tool in all.Where(x => toolNames.Contains(x.Name)))
                db.AgentTools.Add(new AgentTool { AgentId = agent.Id, ToolDefinitionId = tool.Id });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Cấp cho các vai những tool VỪA xuất hiện lần đầu trong bảng định nghĩa (danh sách do
    /// <see cref="ToolDiscoveryService.SyncToolDefinitionsAsync"/> trả về), theo
    /// <see cref="DefaultToolsByRole"/>.
    /// <para>
    /// Vì sao cần: <see cref="AssignDefaultToolsAsync"/> chỉ chạy ở lần seed agent đầu tiên, nên thêm
    /// một tool vào code chỉ có tác dụng trên DB rỗng — mọi môi trường đang chạy nhận được định nghĩa
    /// tool nhưng KHÔNG vai nào được cấp, và triệu chứng là agent im lặng không bao giờ gọi nó.
    /// </para>
    /// <para>
    /// Chỉ cấp cho tool LẦN ĐẦU xuất hiện, và bỏ qua cặp (agent, tool) đã có dòng: quyết định bỏ tick
    /// của admin ở màn Agents không bao giờ bị lần khởi động sau ghi đè.
    /// </para>
    /// </summary>
    private static async Task GrantNewToolsAsync(AppDbContext db, IReadOnlyList<string> newToolNames)
    {
        if (newToolNames.Count == 0)
            return;

        var newTools = await db.ToolDefinitions
            .Where(x => newToolNames.Contains(x.Name))
            .ToListAsync();
        if (newTools.Count == 0)
            return;

        var agents = await db.Agents.Select(x => new { x.Id, x.RoleKey }).ToListAsync();
        var newToolIds = newTools.Select(t => t.Id).ToList();
        var existingPairs = (await db.AgentTools
                .Where(x => newToolIds.Contains(x.ToolDefinitionId))
                .Select(x => new { x.AgentId, x.ToolDefinitionId })
                .ToListAsync())
            .Select(x => (x.AgentId, x.ToolDefinitionId))
            .ToHashSet();

        var granted = 0;
        foreach (var agent in agents)
        {
            if (!DefaultToolsByRole.TryGetValue(agent.RoleKey, out var toolNames))
                continue;

            foreach (var tool in newTools.Where(t => toolNames.Contains(t.Name)))
            {
                if (!existingPairs.Add((agent.Id, tool.Id)))
                    continue;
                db.AgentTools.Add(new AgentTool { AgentId = agent.Id, ToolDefinitionId = tool.Id });
                granted++;
            }
        }

        if (granted > 0)
            await db.SaveChangesAsync();
    }

}
