using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Tools;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICOGenerator.Tests.Tools;

/// <summary>
/// Hai tool ĐIỀU HƯỚNG/SỬA của agent trên code có sẵn:
/// <para>
/// • <c>SearchInFiles</c> — tìm theo NỘI DUNG. Trước khi có nó, vai Developer chỉ có <c>SearchFiles</c>
/// (khớp đường dẫn), nên câu hỏi thường gặp nhất ở bước sửa lỗi — "chỗ nào đang dùng cái này" — phải
/// trả lời bằng cách đọc mò từng file, đốt hết ngân sách bước.
/// </para>
/// <para>
/// • <c>ReplaceInFile</c> — phải TỪ CHỐI khi <c>oldText</c> khớp nhiều chỗ. Bản cũ dùng thẳng
/// <c>string.Replace</c> nên một lời gọi nhắm vào một chỗ lại sửa mọi chỗ giống hệt, và tool vẫn báo
/// thành công: hỏng dữ liệu trong im lặng.
/// </para>
/// </summary>
public class WorkspaceSearchAndReplaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ico-search-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private WorkspaceTools NewTools(out string workspacePath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = _root,
                ["AllowedFileExtensions:0"] = ".cs",
                ["AllowedFileExtensions:1"] = ".json",
                ["AllowedFileExtensions:2"] = ".md"
            })
            .Build();

        // Hai dependency POC không nằm trên đường đi của các tool file (xem GitToolsRepoScopeTests).
        var tools = new WorkspaceTools(config, new WorkspacePathResolver(config), null!, null!);
        tools.SetWorkspace("proj");
        workspacePath = tools.CurrentWorkspacePath;
        return tools;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void SearchInFiles_Returns_Path_Line_And_Text_Of_Content_Matches()
    {
        var tools = NewTools(out var workspace);
        WriteFile(workspace, "src/Order.cs", "public class Order\n{\n    public int OrderId { get; set; }\n}");
        WriteFile(workspace, "src/Cart.cs", "// không liên quan\n");

        var result = tools.SearchInFiles("OrderId");

        Assert.Contains("Order.cs:3:", result);
        Assert.Contains("public int OrderId { get; set; }", result);
        Assert.DoesNotContain("Cart.cs", result);
    }

    [Fact]
    public void SearchInFiles_Is_Case_Insensitive_And_Reports_No_Match_Clearly()
    {
        var tools = NewTools(out var workspace);
        WriteFile(workspace, "src/Order.cs", "var TotalAmount = 0;");

        Assert.Contains("Order.cs:1:", tools.SearchInFiles("totalamount"));
        Assert.Contains("No match", tools.SearchInFiles("KhongCoChuoiNay"));
    }

    // node_modules/bin/obj là nơi mọi từ khoá đều khớp hàng nghìn lần: không lọc thì kết quả tìm kiếm
    // bị một bản sao thư viện đẩy hết file nguồn thật ra ngoài.
    [Fact]
    public void SearchInFiles_Skips_Regenerable_Directories()
    {
        var tools = NewTools(out var workspace);
        WriteFile(workspace, "src/Order.cs", "OrderTotal");
        WriteFile(workspace, "src/node_modules/pkg/index.js", "OrderTotal");
        WriteFile(workspace, "src/obj/Debug/gen.cs", "OrderTotal");

        var result = tools.SearchInFiles("OrderTotal");

        Assert.Contains("Order.cs", result);
        Assert.DoesNotContain("node_modules", result);
        Assert.DoesNotContain("obj", result);
    }

    [Fact]
    public void SearchInFiles_PathFilter_Limits_Scope_And_Rejects_Escape()
    {
        var tools = NewTools(out var workspace);
        WriteFile(workspace, "backend/Order.cs", "Total");
        WriteFile(workspace, "frontend/order.js", "Total");

        var scoped = tools.SearchInFiles("Total", "backend");
        Assert.Contains("Order.cs", scoped);
        Assert.DoesNotContain("order.js", scoped);

        Assert.Contains("outside the workspace", tools.SearchInFiles("Total", "../../etc"));
        Assert.Contains("Folder not found", tools.SearchInFiles("Total", "khong-ton-tai"));
    }

    [Fact]
    public void SearchInFiles_Caps_Results_And_Says_So()
    {
        var tools = NewTools(out var workspace);
        WriteFile(workspace, "src/Big.cs", string.Join("\n", Enumerable.Repeat("Total = 1;", 50)));

        var result = tools.SearchInFiles("Total", maxResults: 3);

        Assert.Equal(3, result.Split('\n').Count(l => l.Contains("Big.cs:")));
        Assert.Contains("truncated", result);
    }

    // Chốt chặn chính: khớp nhiều chỗ ⇒ KHÔNG ghi gì cả và nói rõ số chỗ khớp.
    [Fact]
    public async Task ReplaceInFile_Rejects_Ambiguous_Match_And_Writes_Nothing()
    {
        var tools = NewTools(out var workspace);
        const string original = "var x = 1;\nvar y = 1;\nvar z = 1;";
        WriteFile(workspace, "src/A.cs", original);

        var result = await tools.ReplaceInFile("src/A.cs", "= 1;", "= 2;");

        Assert.Contains("appears 3 times", result);
        Assert.Contains("nothing was written", result);
        Assert.Equal(original, File.ReadAllText(Path.Combine(workspace, "src", "A.cs")));
    }

    [Fact]
    public async Task ReplaceInFile_Replaces_A_Unique_Match()
    {
        var tools = NewTools(out var workspace);
        WriteFile(workspace, "src/A.cs", "var x = 1;\nvar y = 2;");

        var result = await tools.ReplaceInFile("src/A.cs", "var y = 2;", "var y = 3;");

        Assert.Contains("File updated", result);
        Assert.Equal("var x = 1;\nvar y = 3;", File.ReadAllText(Path.Combine(workspace, "src", "A.cs")));
    }

    [Fact]
    public async Task ReplaceInFile_ReplaceAll_Is_Opt_In()
    {
        var tools = NewTools(out var workspace);
        WriteFile(workspace, "src/A.cs", "a;a;a;");

        var result = await tools.ReplaceInFile("src/A.cs", "a;", "b;", replaceAll: true);

        Assert.Contains("3 occurrences replaced", result);
        Assert.Equal("b;b;b;", File.ReadAllText(Path.Combine(workspace, "src", "A.cs")));
    }

    // Chuỗi rỗng khớp ở mọi vị trí: string.Replace sẽ chèn newText vào giữa từng ký tự của file.
    [Fact]
    public async Task ReplaceInFile_Rejects_Empty_OldText()
    {
        var tools = NewTools(out var workspace);
        WriteFile(workspace, "src/A.cs", "abc");

        Assert.Contains("oldText is required", await tools.ReplaceInFile("src/A.cs", "", "x"));
        Assert.Equal("abc", File.ReadAllText(Path.Combine(workspace, "src", "A.cs")));
    }
}
