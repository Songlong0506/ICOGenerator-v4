using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamExport;

/// <summary>
/// Biến đổi một file JSON (appsettings) theo luật trong cấu hình thay vì giữ một bản sao tay: key mới
/// thêm ở repo gốc tự chảy sang repo team, chỉ những chỗ KHÁC nhau mới phải khai báo.
/// </summary>
public static class JsonTransform
{
    public static string Apply(string json, JsonTransformRule rule, Action<string> warn)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException("File JSON rỗng.");

        foreach (var path in rule.Remove)
        {
            var (parent, key) = Locate(root, path, create: false);
            if (parent is null || !parent.ContainsKey(key))
            {
                warn($"Không thấy key '{path}' để xoá — repo gốc đã đổi cấu trúc? Xem lại team-export.json.");
                continue;
            }
            parent.Remove(key);
        }

        foreach (var (path, value) in rule.Set)
        {
            var (parent, key) = Locate(root, path, create: true);
            if (!parent!.ContainsKey(key))
                warn($"Key '{path}' không có ở repo gốc — được thêm mới cho repo team.");
            parent[key] = value?.DeepClone();
        }

        if (!string.IsNullOrEmpty(rule.RemoveKeysStartingWith))
            RemoveKeysStartingWith(root, rule.RemoveKeysStartingWith);

        var text = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        return text + "\n";
    }

    /// <summary>Đường dẫn dạng "Notifications:Email" (giống cách ASP.NET Core đặt tên key cấu hình).</summary>
    private static (JsonObject? Parent, string Key) Locate(JsonNode root, string path, bool create)
    {
        var parts = path.Split(':');
        var current = root.AsObject();
        foreach (var part in parts[..^1])
        {
            if (current[part] is JsonObject next)
            {
                current = next;
            }
            else if (create)
            {
                next = new JsonObject();
                current[part] = next;
                current = next;
            }
            else
            {
                return (null, parts[^1]);
            }
        }
        return (current, parts[^1]);
    }

    private static void RemoveKeysStartingWith(JsonNode? node, string prefix)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                    obj.Remove(key);
                foreach (var child in obj.Select(p => p.Value).ToList())
                    RemoveKeysStartingWith(child, prefix);
                break;
            case JsonArray arr:
                foreach (var child in arr)
                    RemoveKeysStartingWith(child, prefix);
                break;
        }
    }
}
