namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Chuẩn bị <c>04_Implementation/</c> cho một lượt DỰNG MỚI bản demo: chép <c>poc-template.html</c> và
/// seed <c>poc-demo.html</c> từ template với vùng <c>POC_CONTENT</c> thu về một placeholder — agent chỉ sửa
/// vùng nội dung, không phải chép lại cả vỏ (đỡ token mỗi lượt; một ReplaceInFile tất định thay vì tái tạo
/// nguyên khối ~160 dòng vốn luôn trượt "Old text not found").
///
/// <para>
/// Dùng chung cho bước PocPreview của pipeline (<see cref="Workflows.AgentTaskWorker"/>) và cho biến thể
/// thử model (<see cref="Workflows.PocVariantRunner"/>) — hai đường phải seed y hệt nhau, nếu không bảng so
/// sánh model đang so hai điểm xuất phát khác nhau.
/// </para>
/// </summary>
public static class PocDemoSeeder
{
    /// <summary>Đường dẫn template gốc, tính từ ContentRootPath (cùng gốc "Prompts" với PromptTemplateService).</summary>
    public static string TemplatePath(string contentRootPath) =>
        Path.Combine(contentRootPath, "Prompts", "Design", "poc-template.html");

    /// <summary>
    /// Chép template + seed bản demo vào <paramref name="implementationDir"/>. Trả false khi không có
    /// template (cài đặt thiếu file) — bên gọi tự quyết định đó là lỗi hay bỏ qua.
    /// </summary>
    public static async Task<bool> SeedAsync(string templatePath, string implementationDir, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(templatePath))
            return false;

        Directory.CreateDirectory(implementationDir);
        File.Copy(templatePath, Path.Combine(implementationDir, "poc-template.html"), overwrite: true);

        var template = await File.ReadAllTextAsync(templatePath, cancellationToken);
        var seeded = PocTemplate.SeedFromTemplate(template);
        var demoPath = Path.Combine(implementationDir, "poc-demo.html");

        // Marker thiếu/hỏng: chép thô để không bao giờ mất file, nhưng vẫn bỏ khối comment hướng dẫn agent
        // để bản demo phục vụ ra không lộ nó thành chữ trên trang. Comment đầu template là chỉ dẫn cho LLM,
        // không phải nội dung trang (xem PocTemplate.StripDeveloperGuide).
        await File.WriteAllTextAsync(demoPath, PocTemplate.StripDeveloperGuide(seeded ?? template), cancellationToken);
        return true;
    }
}
