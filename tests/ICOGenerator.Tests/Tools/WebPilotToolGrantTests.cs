using System.ComponentModel;
using System.Reflection;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.Registry;
using Xunit;

namespace ICOGenerator.Tests.Tools;

// Chốt an toàn của WebPilot, và là thứ dễ bị phá nhất về sau bằng một dòng thiện chí ("cho nó ghi báo
// cáo ra file cho tiện").
//
// Luật: nội dung web NGOÀI đi thẳng vào ngữ cảnh model trong lúc model đang cầm tool, nên một trang cố
// tình chèn chỉ dẫn chỉ được phép điều khiển đúng cái trình duyệt đó. Cho WebPilot thêm RunCommand là
// biến chữ của người lạ thành lệnh chạy trên máy chủ; cho nó tool file + tool git là mở đường đọc
// workspace rồi đẩy ra ngoài. Chiều ngược lại cũng phải giữ: đừng cấp tool web cho Developer/Tester —
// họ đang cầm sẵn RunCommand và Git.
public class WebPilotToolGrantTests
{
    private static string[] ToolsOf(AgentRoleKey role) =>
        DbInitializer.DefaultAgents.Single(x => x.Role == role).Tools;

    private static readonly string[] WebToolNames = typeof(WebTools)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(m => m.GetCustomAttribute<DescriptionAttribute>() != null)
        .Select(m => m.Name)
        .ToArray();

    [Fact]
    public void WebPilot_GetsEveryWebTool_AndNothingElse()
    {
        var granted = ToolsOf(AgentRoleKey.WebPilot);

        Assert.Equal(WebToolNames.OrderBy(x => x), granted.OrderBy(x => x));
    }

    [Fact]
    public void WebPilot_HasNoCommandGitOrFileTools()
    {
        var forbidden = typeof(CommandTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Concat(typeof(GitTools).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Concat(typeof(WorkspaceTools).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(m => m.GetCustomAttribute<DescriptionAttribute>() != null)
            .Select(m => m.Name)
            .ToHashSet();

        Assert.DoesNotContain(ToolsOf(AgentRoleKey.WebPilot), forbidden.Contains);
    }

    [Fact]
    public void NoOtherRole_GetsAWebTool()
    {
        foreach (var (role, _, _, _, tools) in DbInitializer.DefaultAgents)
        {
            if (role == AgentRoleKey.WebPilot)
                continue;

            Assert.DoesNotContain(tools, WebToolNames.Contains);
        }
    }

    [Fact]
    public void EveryDefaultToolName_ActuallyExists()
    {
        // Tên tool trong bảng seed là chuỗi tự do: gõ sai thì agent lặng lẽ thiếu tool, không lỗi build.
        var known = ToolDiscoveryService.ToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(m => m.GetCustomAttribute<DescriptionAttribute>() != null)
            .Select(m => m.Name)
            .ToHashSet();

        var unknown = DbInitializer.DefaultAgents
            .SelectMany(a => a.Tools.Select(t => (a.Role, Tool: t)))
            .Where(x => !known.Contains(x.Tool))
            .Select(x => $"{x.Role}.{x.Tool}")
            .ToList();

        Assert.True(unknown.Count == 0, "Tên tool không tồn tại trong DefaultAgents: " + string.Join(", ", unknown));
    }
}
