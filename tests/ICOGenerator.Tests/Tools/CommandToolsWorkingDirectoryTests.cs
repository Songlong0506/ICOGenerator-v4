using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Tools;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICOGenerator.Tests.Tools;

// RunCommand chạy ở GỐC workspace nếu không nói gì. Trước đây đó là lựa chọn DUY NHẤT, mà toán tử shell
// và `cd` đều bị chặn — nên prompt bảo agent "chạy dotnet build trong 04_Implementation/src/backend" là
// một chỉ dẫn không thực hiện được. Tham số workingDirectory vá chỗ đó, và phải đi qua đúng chốt chặn
// path-traversal của các tool file chứ không phải một bản kiểm tra viết lại.
public class CommandToolsWorkingDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"icogen-cmd-wd-{Guid.NewGuid():N}");

    [Fact]
    public async Task RunCommand_RunsInTheGivenSubDirectory()
    {
        var tools = NewCommandTools(out var workspacePath);
        var subDir = Path.Combine(workspacePath, "04_Implementation", "src");
        Directory.CreateDirectory(subDir);

        // Lệnh in ra thư mục hiện hành — thứ duy nhất chứng minh được lệnh chạy ĐÚNG chỗ.
        var result = await tools.RunCommand(PrintWorkingDirectoryCommand, "04_Implementation/src");

        Assert.Contains(Path.Combine("04_Implementation", "src"), result);
    }

    [Fact]
    public async Task RunCommand_WithoutWorkingDirectory_StaysAtWorkspaceRoot()
    {
        var tools = NewCommandTools(out var workspacePath);

        var result = await tools.RunCommand(PrintWorkingDirectoryCommand);

        Assert.Contains(Path.GetFileName(workspacePath), result);
        Assert.DoesNotContain("04_Implementation", result);
    }

    // workingDirectory do model sinh ra: cùng chốt chặn với WriteFile/ReadFile, không có đường vòng.
    [Theory]
    [InlineData("../..")]
    [InlineData("04_Implementation/../../../etc")]
    public async Task RunCommand_WithDirectoryEscapingWorkspace_IsBlocked(string workingDirectory)
    {
        var tools = NewCommandTools(out _);

        var result = await tools.RunCommand(PrintWorkingDirectoryCommand, workingDirectory);

        Assert.Contains("Command blocked for security reason", result);
        Assert.Contains("escapes the workspace", result);
    }

    // Thư mục chưa tồn tại (agent gõ nhầm, hoặc bước trước chưa tạo): nói rõ để nó sửa lời gọi, thay vì
    // để Process.Start ném một exception làm hỏng cả lượt chạy.
    [Fact]
    public async Task RunCommand_WithMissingDirectory_ReportsItInsteadOfThrowing()
    {
        var tools = NewCommandTools(out _);

        var result = await tools.RunCommand(PrintWorkingDirectoryCommand, "04_Implementation/src");

        Assert.Contains("Working directory does not exist", result);
    }

    // cmd builtin trên Windows, lệnh thường trên Linux/macOS — cả hai đều in thư mục hiện hành.
    private static string PrintWorkingDirectoryCommand => OperatingSystem.IsWindows() ? "cd" : "pwd";

    private CommandTools NewCommandTools(out string workspacePath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = _root,
                // Allowlist của riêng test: lệnh in thư mục hiện hành không nằm trong AllowedCommands thật
                // (app không cần nó), nhưng nó là cách rẻ nhất để chứng minh lệnh chạy đúng thư mục.
                ["AllowedCommands:0"] = PrintWorkingDirectoryCommand
            })
            .Build();

        var resolver = new WorkspacePathResolver(config);
        // Hai dependency POC không nằm trên đường đi của CommandTools (xem GitToolsRepoScopeTests).
        var workspace = new WorkspaceTools(config, resolver, null!, null!);
        workspace.SetWorkspace("proj-key");
        workspacePath = workspace.CurrentWorkspacePath;

        return new CommandTools(config, workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
