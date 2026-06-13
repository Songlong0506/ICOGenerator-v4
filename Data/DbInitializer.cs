using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
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

        var discovery = scope.ServiceProvider.GetRequiredService<ToolDiscoveryService>();
        await discovery.SyncToolDefinitionsAsync();

        if (!await db.AiModels.AnyAsync())
        {
            db.AiModels.AddRange(
                new AiModel { Name = "Qwen3.6 27B Q3_K_S", Provider = "LM Studio", ModelId = "qwen3.6-27b@q3_k_s", Endpoint = "http://127.0.0.1:1234/v1", ApiKey = "lm-studio", IsDefault = true, ContextWindow = 128000 },
                new AiModel { Name = "DeepSeek V4 Flash", Provider = "DeepSeek", ModelId = "deepseek-v4-flash", Endpoint = "https://api.deepseek.com", ApiKey = "sk-...", ContextWindow = 1000000 }
            );
            await db.SaveChangesAsync();
        }

        if (!await db.Agents.AnyAsync())
        {
            var modelId = await db.AiModels.Where(x => x.IsDefault).Select(x => x.Id).FirstAsync();
            var agents = new[]
            {
                new Agent { Name="BA", RoleKey=AgentRoleKey.BusinessAnalyst, Color="#8B5CF6", AiModelId=modelId, Description="Thu thập và phân tích yêu cầu, viết tài liệu đặc tả nghiệp vụ." },
                new Agent { Name="Tech Lead", RoleKey=AgentRoleKey.TechLead, Color="#3B82F6", AiModelId=modelId, Description="Thiết kế kiến trúc và review kỹ thuật." },
                new Agent { Name="Developer", RoleKey=AgentRoleKey.Developer, Color="#10B981", AiModelId=modelId, Description="Sinh source code, build và sửa lỗi." },
                new Agent { Name="Tester", RoleKey=AgentRoleKey.Tester, Color="#2563EB", AiModelId=modelId, Description="Viết test cases và kiểm thử." },
                new Agent { Name="UI/UX", RoleKey=AgentRoleKey.UiUx, Color="#F97316", AiModelId=modelId, Description="Thiết kế flow và wireframe." },
                new Agent { Name="System", RoleKey=AgentRoleKey.System, Color="#64748B", AiModelId=modelId, Status=AgentStatus.Inactive, Description="System orchestration agent." }
            };
            db.Agents.AddRange(agents);
            await db.SaveChangesAsync();

            await AssignDefaultToolsAsync(db);
        }

        await EnsureAgentRoleKeysAsync(db);

        if (!await db.Projects.AnyAsync())
        {
            var p = new Project { Name="E-commerce Web App", Description="Online store with product management, cart, payment...", Status=ProjectStatus.InProgress, CreatedAt=new DateTime(2024,5,20) };
            db.Projects.AddRange(p,
                new Project { Name="Task Management App", Description="Project management tool for teams", Status=ProjectStatus.Planning, CreatedAt=new DateTime(2024,5,18) },
                new Project { Name="AI Chat Platform", Description="Chat platform with AI assistant", Status=ProjectStatus.InProgress, CreatedAt=new DateTime(2024,5,15) },
                new Project { Name="Fitness Tracking App", Description="Mobile app for tracking workouts and health", Status=ProjectStatus.Completed, CreatedAt=new DateTime(2024,5,10) },
                new Project { Name="Hotel Booking System", Description="Booking system for hotels and accommodations", Status=ProjectStatus.Planning, CreatedAt=new DateTime(2024,5,5) });
            await db.SaveChangesAsync();

            var ba = await db.Agents.FirstAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst);
            db.ProjectDocuments.Add(new ProjectDocument { ProjectId=p.Id, AgentId=ba.Id, Folder="01_Requirement", FileName="01_Project_Overview.md", TokenUsed=4250, Content="# Tổng quan dự án\nDự án E-commerce Web App là nền tảng thương mại điện tử cho phép người dùng xem sản phẩm, thêm vào giỏ hàng, thanh toán và quản lý đơn hàng.\n\n## Mục tiêu\n- Cung cấp trải nghiệm mua sắm trực tuyến mượt mà\n- Quản lý sản phẩm, đơn hàng, người dùng hiệu quả\n- Hỗ trợ thanh toán đa dạng" });
            db.AgentConversations.Add(new AgentConversation { ProjectId=p.Id, AgentId=ba.Id, Message="Đã phân tích yêu cầu và tạo tài liệu tổng quan dự án.", TokenUsed=4250 });
            await db.SaveChangesAsync();
        }
    }

    private static async Task EnsureAgentRoleKeysAsync(AppDbContext db)
    {
        var roleByName = new Dictionary<string, AgentRoleKey>(StringComparer.OrdinalIgnoreCase)
        {
            ["BA"] = AgentRoleKey.BusinessAnalyst,
            ["Tech Lead"] = AgentRoleKey.TechLead,
            ["Developer"] = AgentRoleKey.Developer,
            ["Tester"] = AgentRoleKey.Tester,
            ["UI/UX"] = AgentRoleKey.UiUx,
            ["System"] = AgentRoleKey.System
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
            var agent = await db.Agents.FirstAsync(x => x.RoleKey == roleKey);
            foreach (var tool in all.Where(x => toolNames.Contains(x.Name)))
                db.AgentTools.Add(new AgentTool { AgentId = agent.Id, ToolDefinitionId = tool.Id });
        }

        await Assign(AgentRoleKey.BusinessAnalyst, "ListFiles", "ReadFile", "WriteFile", "SearchFiles");
        await Assign(AgentRoleKey.TechLead, "ListFiles", "ReadFile", "WriteFile", "GitDiff", "GitStatus");
        await Assign(AgentRoleKey.Developer, "ListFiles", "ReadFile", "WriteFile", "ReplaceInFile", "RunCommand", "GitStatus", "GitCommit", "CreateBranch", "PushBranch");
        await Assign(AgentRoleKey.Tester, "ListFiles", "ReadFile", "WriteFile", "RunCommand");
        await Assign(AgentRoleKey.UiUx, "WriteFile", "ReadFile", "ListFiles");
        await db.SaveChangesAsync();
    }
}
