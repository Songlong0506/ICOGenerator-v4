using System.Text.RegularExpressions;
using ICOGenerator.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace ICOGenerator.Tests.Localization;

// Giao diện của app là TIẾNG ANH; ngoại lệ duy nhất là màn Requirements (chat BA với người dùng nghiệp vụ),
// vẫn nói tiếng Việt. Luật đó không tự giữ được: một View mới chép từ View cũ, một thông báo lỗi mới thêm vào
// Text.resx mà quên Text.en.resx — đều lọt tiếng Việt lên màn tiếng Anh mà không ai thấy cho tới khi người
// dùng thấy. Các test ở đây là chỗ bắt nó lúc build. Lý do và cách làm đúng: docs/architecture.md, mục
// "Ngôn ngữ giao diện".
public class UiLanguageTests
{
    // File front-end CHỈ màn Requirements dùng — được phép chở tiếng Việt. Thêm vào đây nghĩa là thêm một
    // ngoại lệ cho luật "app nói tiếng Anh": chỉ làm khi file đó thật sự chỉ phục vụ màn Requirements.
    private static readonly string[] RequirementsOnlyFiles =
    {
        "Views/Requirements/",
        "wwwroot/js/requirements.js",
        "wwwroot/js/requirement-workflow.js",
        "wwwroot/js/table-submission.js",
        "wwwroot/css/requirements.css",
    };

    // Tiền tố khoá resource chỉ thuộc phía yêu cầu (chat BA, tài liệu yêu cầu, bảng chốt, cổng phía yêu cầu)
    // hoặc là chữ nối vào bàn giao cho MODEL. Các khoá này không có bản ở Text.en.resx nên luôn ra tiếng Việt.
    // Mọi khoá KHÁC phải có bản tiếng Anh.
    private static readonly string[] RequirementsOnlyKeyPrefixes =
    {
        "AgentTaskWorker.RunRequirementDraftAsync",
        "BAAgentResolver",
        "BAChatReplyParser",
        "BAChatService",
        "ConfirmPermissionMatrixUseCase",
        "CoverageGroup",
        "CoverageMapItem",
        "Cues",
        "DesignPreparationService",
        "FlowKind",
        "FlowMapBuilder",
        "InterviewTable",
        "MarkdownDocxWriter",
        "PendingConfirmTable",
        "PermissionScope",
        "PocUatGate.Handoff",
        "ProductBriefDraftService",
        "ProjectSourceIngestor",
        "RequirementDocsService",
        "RequirementReadinessGate",
        "RequirementResponseParser",
        "RequirementScreenText",
        "RequirementsController",
        "ReviseBriefFromNotesUseCase",
        "SourceRequestTurn",
        "TableSubmission",
    };

    private static readonly Regex Vietnamese = new(
        "[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ]",
        RegexOptions.IgnoreCase);

    // Comment trong repo viết bằng tiếng Việt (CLAUDE.md) — chỉ chữ NGOÀI comment mới tới tay người dùng.
    private static readonly Regex[] Comments =
    {
        new(@"@\*.*?\*@", RegexOptions.Singleline),
        new(@"<!--.*?-->", RegexOptions.Singleline),
        new(@"/\*.*?\*/", RegexOptions.Singleline),
        new(@"(?<![:'""\w/\\])//.*"),
    };

    [Fact]
    public void ViewsScriptsAndStyles_OutsideTheRequirementsScreen_AreInEnglish()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var (folder, pattern) in new[] { ("Views", "*.cshtml"), ("wwwroot", "*.js"), ("wwwroot", "*.css") })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), pattern, SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/lib/", StringComparison.Ordinal)
                    || RequirementsOnlyFiles.Any(x => relative.StartsWith(x, StringComparison.Ordinal)))
                    continue;

                var lines = StripComments(File.ReadAllText(file)).Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (Vietnamese.IsMatch(lines[i]))
                        offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Chữ tiếng Việt trên màn hình ngoài Requirements — giao diện phải là tiếng Anh:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryResourceKey_OutsideTheRequirementsSide_HasAnEnglishTranslation()
    {
        var root = RepoRoot();
        var english = ResourceKeys(root, "Text.en.resx");

        var missing = ResourceKeys(root, "Text.resx")
            .Where(key => !IsRequirementsOnly(key) && !english.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Khoá có ở Localization/Text.resx nhưng thiếu bản tiếng Anh ở Localization/Text.en.resx:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void TheEnglishFile_HasNoStaleOrRequirementsOnlyKeys()
    {
        var root = RepoRoot();
        var source = ResourceKeys(root, "Text.resx");

        var stray = ResourceKeys(root, "Text.en.resx")
            .Where(key => !source.Contains(key) || IsRequirementsOnly(key))
            .ToList();

        Assert.Empty(stray);
    }

    [Fact]
    public void TheEnglishFile_HasNoVietnameseLeftInIt()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "Localization", "Text.en.resx"));
        var leftovers = Regex.Matches(text, @"<data name=""([^""]+)""[^>]*>\s*<value>([^<]*)</value>")
            .Where(m => Vietnamese.IsMatch(m.Groups[2].Value))
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Empty(leftovers);
    }

    // Chỉ màn Requirements được đổi ngôn ngữ khỏi mặc định. Màn mới gắn [UiLanguage] là mở thêm một ngoại lệ
    // cho luật "app nói tiếng Anh" — phải là quyết định có chủ ý, sửa ở đây và ở tài liệu.
    [Fact]
    public void OnlyTheRequirementsController_SwitchesTheUiLanguage()
    {
        var root = RepoRoot();
        var users = Directory.EnumerateFiles(Path.Combine(root, "Controllers"), "*.cs", SearchOption.AllDirectories)
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\[UiLanguage\("))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(new[] { "RequirementsController.cs" }, users);
        Assert.Equal("en", Loc.DefaultLanguage);
    }

    // Resource filter bọc cả action lẫn lúc render View: chữ đọc qua Loc trong next() phải là tiếng Việt, và
    // ra khỏi request thì ngôn ngữ trả về mặc định chứ không rò sang request sau.
    [Fact]
    public async Task TheAttribute_SwitchesTheLanguageInsideTheRequest_AndRestoresItAfterwards()
    {
        var context = new ResourceExecutingContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new List<IValueProviderFactory>());
        string? inside = null;

        await new UiLanguageAttribute(Loc.SourceLanguage).OnResourceExecutionAsync(context, () =>
        {
            inside = Loc.T("ProjectsController.Create.2");
            return Task.FromResult<ResourceExecutedContext>(null!);
        });

        Assert.Equal("Đã tạo dự án.", inside);
        Assert.Equal(Loc.DefaultLanguage, Loc.Language);
        Assert.Equal("Project created.", Loc.T("ProjectsController.Create.2"));
    }

    private static bool IsRequirementsOnly(string key)
        => RequirementsOnlyKeyPrefixes.Any(p => key == p || key.StartsWith(p + ".", StringComparison.Ordinal));

    private static string StripComments(string text)
    {
        foreach (var comment in Comments)
            text = comment.Replace(text, m => Regex.Replace(m.Value, @"[^\n]", " "));
        return text;
    }

    private static HashSet<string> ResourceKeys(string root, string file)
        => Regex.Matches(File.ReadAllText(Path.Combine(root, "Localization", file)), @"<data name=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ICOGenerator.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Không tìm thấy thư mục gốc của repo từ " + AppContext.BaseDirectory);
    }
}
