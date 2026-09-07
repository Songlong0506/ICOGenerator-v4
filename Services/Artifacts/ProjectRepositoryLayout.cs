using System.Text;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Một REPO ĐÍCH của dự án: thư mục trong workspace mà code được sinh vào, và remote mà nhánh feature
/// sẽ được đẩy lên ở bước Pull Request.
/// </summary>
/// <param name="Label">Tên gọi ngắn dùng trong prompt/log ("backend", "frontend", "src").</param>
/// <param name="RelativePath">Đường dẫn tương đối so với gốc workspace (luôn dùng '/').</param>
/// <param name="RemoteUrl">
/// URL repo của DỰ ÁN (<c>Project.BackendGitUrl</c>/<c>FrontendGitUrl</c>). Null/rỗng = chưa cấu hình:
/// repo vẫn được dựng để commit được, nhưng bước Pull Request sẽ báo không có remote để đẩy.
/// </param>
public record ProjectRepositorySlot(string Label, string RelativePath, string? RemoteUrl);

/// <summary>
/// NƠI DUY NHẤT trả lời câu "một dự án có mấy repo đích, nằm ở đâu, đẩy lên remote nào".
/// <para>
/// Ba chỗ phải nhất trí với nhau về câu trả lời đó, và trước đây không chỗ nào trong ba chỗ ấy biết về
/// nhau: <see cref="ProjectRepositorySeeder"/> (dựng repo), cổng duyệt (đòi đủ URL trước bước Pull
/// Request) và prompt của bước Pull Request (bảo agent bàn giao những repo nào). Tách bảng này ra để
/// thêm/đổi một repo đích là sửa MỘT chỗ.
/// </para>
/// </summary>
public static class ProjectRepositoryLayout
{
    // Khớp WorkspacePathResolver.GetImplementationSourcePath. Dùng '/' vì giá trị này đi vào prompt và
    // vào đối số tool (agent gõ lại đúng chuỗi này); Path.Combine hiểu '/' trên cả Windows lẫn Linux.
    public const string SourceRelativePath = "04_Implementation/src";

    public static readonly string BackendRelativePath = $"{SourceRelativePath}/{BoschTemplateSeeder.BackendFolderName}";
    public static readonly string FrontendRelativePath = $"{SourceRelativePath}/{BoschTemplateSeeder.FrontendFolderName}";

    /// <summary>
    /// Các repo đích của dự án.
    /// <para>
    /// Khung Bosch = HAI repo tách rời (backend .NET và frontend Angular là hai repo thật ở Bosch), nên
    /// bước Pull Request phải mở HAI PR. Không dùng khung Bosch thì agent sinh toàn bộ code vào một cây
    /// duy nhất <c>04_Implementation/src</c> (xem <c>Prompts/Developer/implementation.v1.md</c>) ⇒ một
    /// repo, và <c>FrontendGitUrl</c> không có chỗ dùng — đòi nó ở cổng duyệt chỉ là bắt người vận hành
    /// điền một ô không ai đọc.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ProjectRepositorySlot> Resolve(
        bool useBoschTemplate, string? backendGitUrl, string? frontendGitUrl)
    {
        if (!useBoschTemplate)
            return [new ProjectRepositorySlot("src", SourceRelativePath, Normalize(backendGitUrl))];

        return
        [
            new ProjectRepositorySlot(BoschTemplateSeeder.BackendFolderName, BackendRelativePath, Normalize(backendGitUrl)),
            new ProjectRepositorySlot(BoschTemplateSeeder.FrontendFolderName, FrontendRelativePath, Normalize(frontendGitUrl))
        ];
    }

    /// <summary>Các slot còn THIẾU remote — cổng duyệt dùng để chặn trước bước Pull Request.</summary>
    public static IReadOnlyList<ProjectRepositorySlot> MissingRemotes(IReadOnlyList<ProjectRepositorySlot> slots) =>
        slots.Where(s => string.IsNullOrWhiteSpace(s.RemoteUrl)).ToList();

    /// <summary>
    /// Khối nối vào prompt bước Pull Request: danh sách repo phải bàn giao, dạng TẤT ĐỊNH.
    /// <para>
    /// Đây là thứ duy nhất cho agent biết nó đang đứng trước một hay hai repo và đường dẫn chính xác để
    /// truyền vào <c>repoPath</c>. Không có khối này thì agent phải ĐOÁN đường dẫn — và đoán sai thì
    /// tool trả lỗi "not a git repository root", đốt một vòng lặp cho mỗi lần đoán.
    /// </para>
    /// </summary>
    public static string BuildPromptBlock(IReadOnlyList<ProjectRepositorySlot> slots)
    {
        if (slots.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("# CÁC REPO PHẢI BÀN GIAO");
        sb.AppendLine();
        sb.AppendLine($"Dự án này có {slots.Count} repo. Với **MỖI** repo dưới đây, chạy đủ chuỗi `GitStatus` → `CreateBranch` → `GitCommit` → `OpenPullRequest`, truyền `repoPath` ĐÚNG NGUYÊN VĂN chuỗi in đậm:");
        sb.AppendLine();

        foreach (var slot in slots)
        {
            var remote = string.IsNullOrWhiteSpace(slot.RemoteUrl)
                ? "CHƯA cấu hình remote — vẫn commit, nhưng push sẽ báo lỗi: nêu rõ điều đó ở câu trả lời cuối"
                : slot.RemoteUrl;
            sb.AppendLine($"- **`{slot.RelativePath}`** ({slot.Label}) → remote: {remote}");
        }

        sb.AppendLine();
        sb.AppendLine("Repo nào KHÔNG có thay đổi nào để commit (`GitStatus` sạch) thì bỏ qua repo đó và nói rõ trong câu trả lời cuối, đừng tạo PR rỗng.");
        return sb.ToString();
    }

    // Trim + coi chuỗi trắng như chưa cấu hình (ô nhập ở Agent Dashboard cho phép để trống).
    private static string? Normalize(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url.Trim();
}
