using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Tra nội dung một tài liệu của project theo (versionName, fileName) trên graph Documents đã Include —
/// lấy bản mới nhất, trả chuỗi rỗng khi chưa có. Dùng chung cho các bước sinh tài liệu (draft/spec/technical).
/// </summary>
public static class ProjectDocumentLookup
{
    public static string GetContent(Project project, string fileName, string versionName)
    {
        return project.Documents
            .Where(x => x.VersionName == versionName && x.FileName == fileName)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.Content)
            .FirstOrDefault() ?? "";
    }
}
