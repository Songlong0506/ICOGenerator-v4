namespace ICOGenerator.Tests.Requirements;

/// <summary>
/// Đọc một file prompt THẬT từ đĩa, theo promptKey (<c>"BusinessAnalyst/product-brief.v3.md"</c>).
/// <para>
/// Có mặt vì lớp test "giao ước prompt ↔ hằng số" ngày càng đông: mỗi test như thế phải đọc đúng file mà
/// app nạp lúc chạy, và trước đây mỗi file test tự chép một hàm dò thư mục <c>Prompts/</c> của riêng nó.
/// Test mới dùng chỗ này; các bản chép cũ dọn dần khi có dịp sửa file tương ứng.
/// </para>
/// </summary>
internal static class PromptFixture
{
    public static string Read(string promptKey)
        => File.ReadAllText(Path.Combine(Root(), promptKey.Replace('/', Path.DirectorySeparatorChar)));

    // Prompts/ được copy vào output của app rồi flow sang bin của test qua ProjectReference; môi trường
    // build không copy transitives thì đi ngược từ BaseDirectory lên repo root.
    public static string Root()
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
