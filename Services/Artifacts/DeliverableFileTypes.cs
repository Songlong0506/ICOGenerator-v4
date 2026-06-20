namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Phân loại file deliverable trong workspace: file văn bản nào xem trực tiếp được (an toàn,
/// phục vụ dạng text/plain) và thư mục nào bỏ qua khi liệt kê (dependency/build output/vcs).
/// </summary>
public static class DeliverableFileTypes
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".cs", ".csproj", ".sln", ".json", ".js", ".css",
        ".html", ".htm", ".sql", ".yml", ".yaml", ".xml", ".config"
    };

    /// <summary>Thư mục không phải sản phẩm — bỏ qua để tránh "node_modules" làm sập việc liệt kê.</summary>
    public static readonly IReadOnlyCollection<string> SkipDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", ".vs", "dist"
    };

    /// <summary>File văn bản (hoặc không có phần mở rộng, vd README) → xem trực tiếp dạng text.</summary>
    public static bool IsTextPreviewable(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) || TextExtensions.Contains(ext);
    }
}
