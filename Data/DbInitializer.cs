using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Tools.Registry;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        await RecoverOrphanedTasksAsync(db);

        var apiKeyProtector = scope.ServiceProvider.GetRequiredService<IApiKeyProtector>();
        await EncryptLegacyApiKeysAsync(db, apiKeyProtector);

        var discovery = scope.ServiceProvider.GetRequiredService<ToolDiscoveryService>();
        await discovery.SyncToolDefinitionsAsync();

        if (!await db.AiModels.AnyAsync())
        {
            db.AiModels.AddRange(
                new AiModel { Name = "Qwen3.6 27B Q3_K_S", Provider = "LM Studio", ModelId = "qwen3.6-27b@q3_k_s", Endpoint = "http://127.0.0.1:1234/v1", ApiKey = "lm-studio", ContextWindow = 128000 },
                // ApiKey để TRỐNG (không placeholder): seed chuỗi giả sẽ bị value-converter mã hóa và lưu,
                // khiến model "đám mây" trông như đã cấu hình nhưng mọi lời gọi đều 401.
                new AiModel { Name = "DeepSeek V4 Flash", Provider = "DeepSeek", ModelId = "deepseek-v4-flash", Endpoint = "https://api.deepseek.com", ApiKey = "", ContextWindow = 1000000 }
            );
            await db.SaveChangesAsync();
        }

        if (!await db.Agents.AnyAsync())
        {
            // Gán thủ công model local (Qwen) cho agent seed, fallback model đầu tiên nếu seed thay đổi.
            var modelId = await db.AiModels
                .OrderByDescending(x => x.Name == "Qwen3.6 27B Q3_K_S")
                .ThenBy(x => x.Name)
                .Select(x => x.Id)
                .FirstAsync();
            var agents = new[]
            {
                new Agent { Name="BA", RoleKey=AgentRoleKey.BusinessAnalyst, Color="#8B5CF6", AiModelId=modelId, Description="Thu thập và phân tích yêu cầu, viết tài liệu đặc tả nghiệp vụ." },
                new Agent { Name="Tech Lead", RoleKey=AgentRoleKey.TechLead, Color="#3B82F6", AiModelId=modelId, Description="Thiết kế kiến trúc và review kỹ thuật." },
                new Agent { Name="Developer", RoleKey=AgentRoleKey.Developer, Color="#10B981", AiModelId=modelId, Description="Sinh source code, build và sửa lỗi." },
                new Agent { Name="Tester", RoleKey=AgentRoleKey.Tester, Color="#2563EB", AiModelId=modelId, Description="Viết test cases và kiểm thử." },
                new Agent { Name="UI/UX", RoleKey=AgentRoleKey.UiUx, Color="#F97316", AiModelId=modelId, Description="Thiết kế flow và wireframe." }
            };
            db.Agents.AddRange(agents);
            await db.SaveChangesAsync();

            await AssignDefaultToolsAsync(db);
        }

        await RemoveLegacySystemAgentAsync(db);
        await EnsureAgentRoleKeysAsync(db);
        await EnsureRoleToolAsync(db, AgentRoleKey.Developer, "SetPocContent");

        if (!await db.Projects.AnyAsync())
        {
            var p = new Project { Name="E-commerce Web App", Description="Online store with product management, cart, payment...", Status=ProjectStatus.InProgress, CreatedAt=new DateTime(2024,5,20) };
            db.Projects.AddRange(p,
                new Project { Name="Task Management App", Description="Project management tool for teams", Status=ProjectStatus.Planning, CreatedAt=new DateTime(2024,5,18) },
                new Project { Name="AI Chat Platform", Description="Chat platform with AI assistant", Status=ProjectStatus.InProgress, CreatedAt=new DateTime(2024,5,15) },
                new Project { Name="Fitness Tracking App", Description="Mobile app for tracking workouts and health", Status=ProjectStatus.Completed, CreatedAt=new DateTime(2024,5,10) },
                new Project { Name="Hotel Booking System", Description="Booking system for hotels and accommodations", Status=ProjectStatus.Planning, CreatedAt=new DateTime(2024,5,5) });
            await db.SaveChangesAsync();

            var ba = await db.Agents.FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst);
            if (ba != null)
            {
                db.ProjectDocuments.Add(new ProjectDocument { ProjectId=p.Id, AgentId=ba.Id, Folder="01_Requirement", FileName="01_Project_Overview.md", TokenUsed=4250, Content="# Tổng quan dự án\nDự án E-commerce Web App là nền tảng thương mại điện tử cho phép người dùng xem sản phẩm, thêm vào giỏ hàng, thanh toán và quản lý đơn hàng.\n\n## Mục tiêu\n- Cung cấp trải nghiệm mua sắm trực tuyến mượt mà\n- Quản lý sản phẩm, đơn hàng, người dùng hiệu quả\n- Hỗ trợ thanh toán đa dạng" });
                db.AgentConversations.Add(new AgentConversation { ProjectId=p.Id, AgentId=ba.Id, Message="Đã phân tích yêu cầu và tạo tài liệu tổng quan dự án.", TokenUsed=4250 });
                await db.SaveChangesAsync();
            }
        }
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

    // Mã hóa một lần ApiKey plaintext còn sót (bản cài cũ). Đọc thô bằng ADO.NET để bỏ qua value converter (vốn tự giải mã).
    private static async Task EncryptLegacyApiKeysAsync(AppDbContext db, IApiKeyProtector protector)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync();

        try
        {
            var legacy = new List<(Guid Id, string ApiKey)>();

            await using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT Id, ApiKey FROM AiModels";
                await using var reader = await read.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (reader.IsDBNull(1))
                        continue;

                    var apiKey = reader.GetString(1);
                    if (!protector.IsProtected(apiKey))
                        legacy.Add((reader.GetGuid(0), apiKey));
                }
            }

            foreach (var (id, apiKey) in legacy)
            {
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE AiModels SET ApiKey = @key WHERE Id = @id";

                var keyParam = update.CreateParameter();
                keyParam.ParameterName = "@key";
                keyParam.Value = protector.Protect(apiKey);
                update.Parameters.Add(keyParam);

                var idParam = update.CreateParameter();
                idParam.ParameterName = "@id";
                idParam.Value = id;
                update.Parameters.Add(idParam);

                await update.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    // Gỡ "System" agent — vai trò orchestration chưa từng được nối vào DeliveryPipeline/orchestrator, seed ở
    // trạng thái Inactive và không được gán tool. Seed chỉ chạy khi bảng Agents rỗng nên các bản cài cũ đã có
    // sẵn một dòng; phải xóa thủ công ở đây (trước EnsureAgentRoleKeysAsync — chỗ materialize toàn bộ agent).
    // RoleKey lưu dạng string nên so theo literal "System"; FK AgentTools→Agents là CASCADE nên link tool (nếu
    // có) tự xóa, chỉ bỏ qua khi agent lỡ có log/hội thoại (FK Restrict) để không vừa hỏng audit vừa crash startup.
    private static async Task RemoveLegacySystemAgentAsync(AppDbContext db)
    {
        const string sql = """
            DELETE FROM Agents
            WHERE RoleKey = {0}
              AND NOT EXISTS (SELECT 1 FROM AgentModelCallLogs c WHERE c.AgentId = Agents.Id)
              AND NOT EXISTS (SELECT 1 FROM AgentConversations v WHERE v.AgentId = Agents.Id)
            """;
        await db.Database.ExecuteSqlRawAsync(sql, "System");
    }

    private static async Task EnsureAgentRoleKeysAsync(AppDbContext db)
    {
        var roleByName = new Dictionary<string, AgentRoleKey>(StringComparer.OrdinalIgnoreCase)
        {
            ["BA"] = AgentRoleKey.BusinessAnalyst,
            ["Tech Lead"] = AgentRoleKey.TechLead,
            ["Developer"] = AgentRoleKey.Developer,
            ["Tester"] = AgentRoleKey.Tester,
            ["UI/UX"] = AgentRoleKey.UiUx
        };

        var agents = await db.Agents.ToListAsync();
        var changed = false;
        foreach (var agent in agents)
        {
            if (!roleByName.TryGetValue(agent.Name, out var roleKey) || agent.RoleKey == roleKey)
                continue;

            agent.RoleKey = roleKey;
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    private static async Task AssignDefaultToolsAsync(AppDbContext db)
    {
        var all = await db.ToolDefinitions.ToListAsync();
        async Task Assign(AgentRoleKey roleKey, params string[] toolNames)
        {
            // FirstOrDefaultAsync (không FirstAsync) để role thiếu agent không ném và crash startup — gán tool seed không được làm hỏng MigrateAsync.
            var agent = await db.Agents.FirstOrDefaultAsync(x => x.RoleKey == roleKey);
            if (agent == null)
                return;
            foreach (var tool in all.Where(x => toolNames.Contains(x.Name)))
                db.AgentTools.Add(new AgentTool { AgentId = agent.Id, ToolDefinitionId = tool.Id });
        }

        await Assign(AgentRoleKey.BusinessAnalyst, "ListFiles", "ReadFile", "WriteFile", "SearchFiles");
        await Assign(AgentRoleKey.TechLead, "ListFiles", "ReadFile", "WriteFile", "GitDiff", "GitStatus");
        await Assign(AgentRoleKey.Developer, "ListFiles", "ReadFile", "WriteFile", "ReplaceInFile", "SetPocContent", "RunCommand", "GitStatus", "GitCommit", "CreateBranch", "PushBranch");
        await Assign(AgentRoleKey.Tester, "ListFiles", "ReadFile", "WriteFile", "RunCommand");
        await Assign(AgentRoleKey.UiUx, "WriteFile", "ReadFile", "ListFiles");
        await db.SaveChangesAsync();
    }

    // Idempotently grants a tool to a role on existing databases (AssignDefaultToolsAsync only runs on a fresh seed, so new tools would never reach already-seeded agents).
    private static async Task EnsureRoleToolAsync(AppDbContext db, AgentRoleKey roleKey, string toolName)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(x => x.RoleKey == roleKey);
        var tool = await db.ToolDefinitions.FirstOrDefaultAsync(x => x.Name == toolName);
        if (agent == null || tool == null)
            return;

        var alreadyAssigned = await db.AgentTools
            .AnyAsync(x => x.AgentId == agent.Id && x.ToolDefinitionId == tool.Id);
        if (alreadyAssigned)
            return;

        db.AgentTools.Add(new AgentTool { AgentId = agent.Id, ToolDefinitionId = tool.Id });
        await db.SaveChangesAsync();
    }
}
