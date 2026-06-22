using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Tools.Registry;
using ICOGenerator.Services.Workflows;
using ICOGenerator.Services.Workflows.Maf;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        var useMafEngine = scope.ServiceProvider.GetRequiredService<MafWorkflowPolicy>().UseMafEngine;
        await RecoverOrphanedTasksAsync(db, useMafEngine);

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
    private static async Task RecoverOrphanedTasksAsync(AppDbContext db, bool useMafEngine)
    {
        var now = DateTime.UtcNow;

        var orphaned = await db.AgentTasks
            .Include(x => x.WorkflowRun)
            .Where(x => x.Status == AgentTaskStatus.Running)
            .ToListAsync();

        foreach (var task in orphaned)
        {
            // MAF engine: a Running delivery task was abandoned mid-pump by the restart. Mark it Failed; the
            // RUN is re-queued below so the workflow engine resumes from its last checkpoint (re-running the
            // stage with a fresh task) — do NOT re-queue the task to the legacy AgentTaskWorker.
            if (useMafEngine && task.Type != AgentTaskType.RequirementAnalysis)
            {
                task.Status = AgentTaskStatus.Failed;
                task.Error = "Bị gián đoạn bởi việc khởi động lại; workflow engine sẽ chạy lại bước này.";
                task.FinishedAt = now;
                continue;
            }

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

        // MAF engine: re-queue every Running delivery run so MafWorkflowWorker resumes it from its checkpoint
        // (also covers a run interrupted between stages, where no Running task exists to key off above).
        if (useMafEngine)
        {
            var runningDeliveryRuns = await db.WorkflowRuns
                .Where(r => r.Status == WorkflowRunStatus.Running && DeliveryPipeline.DeliveryStages.Contains(r.CurrentStage))
                .ToListAsync();

            foreach (var run in runningDeliveryRuns)
                run.Status = WorkflowRunStatus.Queued;
        }

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
        await Assign(AgentRoleKey.Developer, "ListFiles", "ReadFile", "WriteFile", "WriteFiles", "ReplaceInFile", "SetPocContent", "RunCommand", "GitStatus", "GitCommit", "CreateBranch", "PushBranch");
        await Assign(AgentRoleKey.Tester, "ListFiles", "ReadFile", "WriteFile", "RunCommand");
        await Assign(AgentRoleKey.UiUx, "WriteFile", "ReadFile", "ListFiles");
        await db.SaveChangesAsync();
    }
}
