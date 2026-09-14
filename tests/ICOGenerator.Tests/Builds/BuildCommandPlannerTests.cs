using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Builds;
using Xunit;

namespace ICOGenerator.Tests.Builds;

/// <summary>
/// Cổng biên dịch phải tự dò ra "build cái gì" từ chính nội dung workspace: prompt cho agent tự chọn
/// stack (.NET hoặc Node thuần), còn dự án khung Bosch thì luôn là .NET + Angular. Một danh sách lệnh
/// khai cứng sẽ hoặc chạy dotnet build trong cây Node, hoặc bỏ sót hẳn phần frontend.
/// </summary>
public class BuildCommandPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ico-plan-" + Guid.NewGuid().ToString("N"));

    public BuildCommandPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string relative, string content = "")
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static ProjectRepositorySlot SingleSlot() =>
        new("src", ProjectRepositoryLayout.SourceRelativePath, null);

    [Fact]
    public void Plans_Dotnet_Build_For_A_Solution()
    {
        Write("04_Implementation/src/App.sln");
        Write("04_Implementation/src/Api/Api.csproj");

        var plan = BuildCommandPlanner.Plan(_root, [SingleSlot()]);

        var command = Assert.Single(plan);
        Assert.Equal(["dotnet", "build", "App.sln", "--nologo"], command.Args);
        Assert.Equal("04_Implementation/src", command.WorkingDirectory);
    }

    // Không có .sln thì build .csproj — và truyền TÊN FILE, vì thư mục có hai .csproj thì "dotnet build"
    // trần báo lỗi "found more than one project": một lỗi hạ tầng đội lốt lỗi biên dịch.
    [Fact]
    public void Falls_Back_To_The_Shallowest_Csproj()
    {
        Write("04_Implementation/src/Api/Api.csproj");
        Write("04_Implementation/src/Api/Nested/Deep/Other.csproj");

        var command = Assert.Single(BuildCommandPlanner.Plan(_root, [SingleSlot()]));

        Assert.Equal(["dotnet", "build", "Api.csproj", "--nologo"], command.Args);
        Assert.Equal("04_Implementation/src/Api", command.WorkingDirectory);
    }

    [Fact]
    public void Plans_Npm_Install_Then_Build_When_A_Build_Script_Exists()
    {
        Write("04_Implementation/src/package.json", """{ "scripts": { "build": "ng build" } }""");

        var plan = BuildCommandPlanner.Plan(_root, [SingleSlot()]);

        Assert.Equal(2, plan.Count);
        Assert.True(plan[0].IsInstall);
        Assert.Equal(["npm", "install", "--no-audit", "--no-fund"], plan[0].Args);
        Assert.Equal(["npm", "run", "build"], plan[1].Args);
        Assert.False(plan[1].IsInstall);
    }

    // Không có script build thì chạy "npm install" một mình chẳng chứng minh được gì về code vừa sinh.
    [Fact]
    public void Skips_Node_When_There_Is_No_Build_Script()
    {
        Write("04_Implementation/src/package.json", """{ "scripts": { "start": "node index.js" } }""");

        Assert.Empty(BuildCommandPlanner.Plan(_root, [SingleSlot()]));
    }

    [Fact]
    public void Bosch_Layout_Plans_Both_Backend_And_Frontend()
    {
        Write("04_Implementation/src/backend/Backend.sln");
        Write("04_Implementation/src/frontend/package.json", """{ "scripts": { "build": "ng build" } }""");

        var slots = ProjectRepositoryLayout.Resolve(useBoschTemplate: true, "http://git/be.git", "http://git/fe.git");
        var plan = BuildCommandPlanner.Plan(_root, slots);

        Assert.Equal(3, plan.Count);
        Assert.Contains(plan, c => c.Args[0] == "dotnet" && c.WorkingDirectory.EndsWith("backend"));
        Assert.Contains(plan, c => c.IsInstall && c.WorkingDirectory.EndsWith("frontend"));
        Assert.Contains(plan, c => c.Args is ["npm", "run", "build"]);
    }

    // Một bản sao dự án nằm trong node_modules không phải thứ cần build — và nếu nó được chọn thì cổng
    // chấm sai hẳn đối tượng.
    [Fact]
    public void Ignores_Projects_Inside_Regenerable_Directories()
    {
        Write("04_Implementation/src/node_modules/pkg/Fake.csproj");

        Assert.Empty(BuildCommandPlanner.Plan(_root, [SingleSlot()]));
    }

    // Thư mục code chưa tồn tại (bước Implementation hỏng giữa chừng) ⇒ không có lệnh nào, và cổng build
    // coi đó là BỎ QUA chứ không phải trượt.
    [Fact]
    public void Returns_Empty_When_The_Source_Folder_Does_Not_Exist()
    {
        Assert.Empty(BuildCommandPlanner.Plan(_root, [SingleSlot()]));
    }
}
