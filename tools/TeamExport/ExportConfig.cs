using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileSystemGlobbing;

namespace TeamExport;

/// <summary>Luật xuất bản cho team, đọc từ <c>tools/TeamExport/team-export.json</c>.</summary>
public sealed class ExportConfig
{
    /// <summary>Glob (tương đối gốc repo) các file KHÔNG đưa sang repo team.</summary>
    public List<string> Exclude { get; set; } = new();

    /// <summary>Đuôi file sẽ bị xoá comment.</summary>
    public List<string> StripCommentExtensions { get; set; } = new();

    /// <summary>Glob các file giữ nguyên comment dù đúng đuôi.</summary>
    public List<string> StripCommentExclude { get; set; } = new();

    /// <summary>Comment chứa một trong các chuỗi này thì giữ lại (vd "&lt;auto-generated").</summary>
    public List<string> KeepCommentsContaining { get; set; } = new();

    /// <summary>Luật biến đổi file JSON, khoá = đường dẫn file.</summary>
    public Dictionary<string, JsonTransformRule> JsonTransforms { get; set; } = new();

    /// <summary>Thư mục (tương đối file cấu hình) chứa file chép đè lên bản xuất, vd README riêng cho team.</summary>
    public string? OverlayDirectory { get; set; }

    /// <summary>Glob các file chỉ có ở repo team (CI pipeline...) — không bị xoá khi đồng bộ.</summary>
    public List<string> PreserveInTarget { get; set; } = new();

    public static ExportConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ExportConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException($"Không đọc được {path}.");
    }

    public Func<string, bool> Matcher(IEnumerable<string> globs)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(globs);
        return path => matcher.Match(path).HasMatches;
    }
}

public sealed class JsonTransformRule
{
    /// <summary>Xoá mọi key bắt đầu bằng chuỗi này ở mọi cấp (vd "_" để bỏ các "_note").</summary>
    public string? RemoveKeysStartingWith { get; set; }

    /// <summary>Đường dẫn key cần xoá, dạng "Notifications:Email".</summary>
    public List<string> Remove { get; set; } = new();

    /// <summary>Key cần đặt giá trị khác, dạng "Authentication:Provider": "IdentityServer".</summary>
    public Dictionary<string, JsonNode?> Set { get; set; } = new();
}
