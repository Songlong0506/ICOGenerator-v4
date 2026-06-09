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
                new AiModel { Name = "DeepSeek V4 Flash", Provider = "DeepSeek", ModelId = "deepseek-v4-flash", Endpoint = "https://api.deepseek.com", ApiKey = "sk-90ac1cc986b142f4bef042580d651be7", ContextWindow = 1000000 }
            );
            await db.SaveChangesAsync();
        }

        if (!await db.Agents.AnyAsync())
        {
            var modelId = await db.AiModels.Where(x => x.IsDefault).Select(x => x.Id).FirstAsync();
            var agents = new[]
            {
                new Agent { Name="BA", RoleTitle="Business Analyst", RoleKey=AgentRoleKey.BusinessAnalyst, Color="#8B5CF6", AiModelId=modelId, Description="Thu thập và phân tích yêu cầu, viết tài liệu đặc tả nghiệp vụ.", Instruction="Bạn là BA. Hãy hỏi rõ yêu cầu, tạo BRD, SRS, user stories, acceptance criteria." },
                new Agent { Name="Tech Lead", RoleTitle="Technical Lead", RoleKey=AgentRoleKey.TechLead, Color="#3B82F6", AiModelId=modelId, Description="Thiết kế kiến trúc và review kỹ thuật.", Instruction="Bạn là Tech Lead. Hãy đề xuất kiến trúc, phân tích technical risks và review solution." },
                new Agent { Name="Developer", RoleTitle="Developer", RoleKey=AgentRoleKey.Developer, Color="#10B981", AiModelId=modelId, Description="Sinh source code, build và sửa lỗi.", Instruction="Bạn là Developer Agent chuyên tạo POC demo cho client.\n\nMục tiêu duy nhất:\n- Đọc AI Design Spec được cung cấp.\n- Sinh ra đúng 1 file HTML POC để demo cho client.\n- File output phải là: poc-demo.html\n\nQuy tắc bắt buộc:\n1. Chỉ tạo 1 file duy nhất: poc-demo.html.\n2. Không tạo project .NET, Angular, React, package.json, csproj, controller, service, migration.\n3. Không chạy vòng lặp nhiều bước.\n4. Không gọi API nhiều lần nếu file đã được tạo thành công.\n5. Không build, không test, không chạy npm, không chạy dotnet.\n6. Không tạo backend thật.\n7. Không tạo database thật.\n8. Không sửa BRD/SRS/FSD/UserStories/AIDesignSpec.\n9. Không hỏi lại user.\n10. Sau khi ghi file thành công thì trả final result ngay.\n\nYêu cầu cho file HTML:\n- Là single-page HTML.\n- Có inline CSS.\n- Có inline JavaScript nếu cần.\n- Không phụ thuộc internet.\n- Không dùng CDN.\n- Thiết kế đẹp, chuyên nghiệp, dùng style enterprise dashboard.\n- Có left sidebar navigation.\n- Có header.\n- Có các màn hình/tab chính theo AI Design Spec.\n- Có mock data.\n- Có table, cards, status badges, modal create/edit giả lập nếu phù hợp.\n- Các button có thể demo bằng JavaScript đơn giản.\n- Nội dung phải đủ để client hiểu flow chính.\n\nTool usage:\n- Chỉ được dùng tool WriteFile một lần để tạo poc-demo.html.\n- Sau đó dùng final response.\n- Không dùng RunCommand trừ khi được yêu cầu rõ ràng.\n- Không dùng GitCommit, PushBranch, CreateBranch.\n- Không dùng ReplaceInFile.\n- Không dùng ListFiles nếu không cần.\n\nOutput:\n- Nếu tạo file thành công, trả:\n  \"POC demo created successfully: poc-demo.html\"" },
                new Agent { Name="Tester", RoleTitle="QA Engineer", RoleKey=AgentRoleKey.Tester, Color="#2563EB", AiModelId=modelId, Description="Viết test cases và kiểm thử.", Instruction="Bạn là Tester. Hãy tạo test cases, kiểm tra acceptance criteria và report bugs." },
                new Agent { Name="UI/UX", RoleTitle="Designer", RoleKey=AgentRoleKey.UiUx, Color="#F97316", AiModelId=modelId, Description="Thiết kế flow và wireframe.", Instruction="Bạn là UI/UX designer. Hãy tạo user flow, wireframe notes và UI guideline." },
                new Agent { Name="System", RoleTitle="System Agent", RoleKey=AgentRoleKey.System, Color="#64748B", AiModelId=modelId, Status=AgentStatus.Inactive, Description="System orchestration agent.", Instruction="System orchestration agent." }
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
