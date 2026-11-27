using Xunit;

namespace ICOGenerator.Tests.Llm;

/// <summary>
/// Chặn <c>new JsonArray(x)</c> với đối số lẻ. System.Text.Json bản mới có thêm overload
/// <c>JsonArray(params ReadOnlySpan&lt;JsonNode?&gt;)</c>: SDK 8.0.4xx (máy dev, CI GitHub) im lặng chọn overload mảng,
/// còn SDK 8.0.3xx (Roslyn 4.10, agent build Azure của repo team) chọn overload span rồi báo
/// <c>CS8652: params collections is in Preview</c> — build local xanh mà pipeline team đỏ.
/// Viết <c>new JsonArray { x, y }</c> (collection initializer, đi qua <c>Add</c>) hoặc truyền hẳn một mảng
/// (<c>new JsonArray(items.ToArray())</c>).
/// </summary>
public class JsonArrayParamsCompatTests
{
    private const string Ctor = "new JsonArray(";

    [Fact]
    public void NoJsonArrayCtorWithExpandedParams()
    {
        var root = RepoRoot();
        var self = Path.Combine("tests", "ICOGenerator.Tests", "Llm", nameof(JsonArrayParamsCompatTests) + ".cs");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj") || rel == self)
                continue;

            var text = File.ReadAllText(file);
            for (var i = text.IndexOf(Ctor, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(Ctor, i + Ctor.Length, StringComparison.Ordinal))
            {
                var arg = BalancedArgument(text, i + Ctor.Length).Trim();
                if (arg.Length > 0 && !arg.EndsWith(".ToArray()", StringComparison.Ordinal))
                {
                    var line = text.AsSpan(0, i).Count('\n') + 1;
                    offenders.Add($"{rel.Replace('\\', '/')}:{line}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "new JsonArray(x) không build được trên SDK 8.0.3xx (CS8652) — dùng new JsonArray { x } hoặc truyền mảng: "
            + string.Join(", ", offenders));
    }

    /// <summary>Đoạn từ <paramref name="start"/> tới dấu <c>)</c> đóng cân bằng (đủ dùng: không parse chuỗi chứa ngoặc).</summary>
    private static string BalancedArgument(string text, int start)
    {
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return text[start..i];
        }

        return text[start..];
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
