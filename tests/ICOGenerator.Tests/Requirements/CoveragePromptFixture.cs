using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Tests.Requirements;

/// <summary>
/// Đọc file prompt coverage THẬT từ đĩa. Nhiều test chốt rằng code khớp với file prompt (danh sách nhóm,
/// câu mở đầu của từng nhóm) — dùng chung một hàm đọc để chúng không thể trỏ vào hai bản khác nhau.
/// </summary>
internal static class CoveragePromptFixture
{
    public static string Read() => ReadPrompt(CoverageChecklist.CoveragePromptPath);

    /// <summary>Đọc MỘT file prompt bất kỳ theo đường dẫn tương đối trong <c>Prompts/</c> (vd
    /// <c>BusinessAnalyst/product-brief-note-revision.v1.md</c>) — dùng chung một cách tìm thư mục.</summary>
    public static string ReadPrompt(string relativePath) => File.ReadAllText(Path.Combine(
        FindPromptsRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // Prompts/ được copy vào output của app và flow sang bin của test qua ProjectReference; nếu môi trường
    // build không copy transitives thì đi ngược từ BaseDirectory lên repo root.
    private static string FindPromptsRoot()
    {
        var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts");
        if (Directory.Exists(Path.Combine(fromBin, "BusinessAnalyst")))
            return fromBin;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Prompts");
            if (Directory.Exists(Path.Combine(candidate, "BusinessAnalyst")))
                return candidate;
        }

        throw new DirectoryNotFoundException("Không tìm thấy thư mục Prompts từ " + AppContext.BaseDirectory);
    }
}
