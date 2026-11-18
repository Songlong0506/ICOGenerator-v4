using System.Text.Json.Nodes;
using TeamExport;
using Xunit;

namespace ICOGenerator.Tests.TeamExport;

public class ExportConfigTests
{
    // Luật biến đổi appsettings cho repo team trỏ tới key theo TÊN. Đổi tên/xoá một key ở đây mà quên sửa
    // tools/TeamExport/team-export.json thì lần xuất sau lặng lẽ bỏ sót (vd section Email cũ sống lại bên
    // team dưới tên mới). Test này bắt chuyện đó lúc build, thay vì lúc team phát hiện.
    [Fact]
    public void TeamExportRulesStillMatchAppsettings()
    {
        var root = RepoRoot();
        var config = ExportConfig.Load(Path.Combine(root, "tools", "TeamExport", "team-export.json"));
        var warnings = new List<string>();

        foreach (var (file, rule) in config.JsonTransforms)
        {
            var json = File.ReadAllText(Path.Combine(root, file));
            var result = JsonNode.Parse(JsonTransform.Apply(json, rule, warnings.Add))!;
            foreach (var path in rule.Remove)
                Assert.Null(Find(result, path));
        }

        Assert.Empty(warnings);
    }

    [Fact]
    public void ExportExcludesEveryNonAppProjectFromTheSolution()
    {
        var root = RepoRoot();
        var config = ExportConfig.Load(Path.Combine(root, "tools", "TeamExport", "team-export.json"));
        var sln = File.ReadAllText(Path.Combine(root, "ICOGenerator.sln"));

        var filtered = SolutionFilter.Filter(sln, config.Matcher(config.Exclude));

        Assert.Contains("\"ICOGenerator.csproj\"", filtered);
        Assert.DoesNotContain("Tests", filtered);
        Assert.DoesNotContain("TeamExport", filtered);
    }

    [Fact]
    public void JsonTransform_RemovesSetsAndDropsNoteKeys()
    {
        var json = """{ "A": { "_note": "x", "B": 1, "C": 2 }, "D": [ { "_n": 1, "E": 3 } ] }""";
        var rule = new JsonTransformRule
        {
            RemoveKeysStartingWith = "_",
            Remove = ["A:C", "A:Missing"],
            Set = new() { ["A:B"] = JsonValue.Create("v"), ["F:G"] = JsonValue.Create(true) }
        };
        var warnings = new List<string>();

        var result = JsonNode.Parse(JsonTransform.Apply(json, rule, warnings.Add))!;

        Assert.Equal("""{"A":{"B":"v"},"D":[{"E":3}],"F":{"G":true}}""", result.ToJsonString());
        Assert.Equal(2, warnings.Count); // A:Missing không tồn tại, F:G là key mới
    }

    private static JsonNode? Find(JsonNode root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split(':'))
            node = node is JsonObject obj && obj.TryGetPropertyValue(part, out var next) ? next : null;
        return node;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ICOGenerator.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Không tìm thấy repo root từ " + AppContext.BaseDirectory);
    }
}
