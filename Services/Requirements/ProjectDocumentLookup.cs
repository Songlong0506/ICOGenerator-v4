using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Tra nội dung một tài liệu của project theo (versionName, fileName) trên graph Documents đã Include —
/// lấy bản mới nhất, trả chuỗi rỗng khi chưa có. Dùng chung cho các bước sinh tài liệu (draft/spec/technical).
/// </summary>
public static class ProjectDocumentLookup
{
    /// <summary>Phiên bản ĐÃ DUYỆT mới nhất của một tài liệu (tên phiên bản + nội dung).</summary>
    public sealed record ApprovedDocument(string VersionName, string Content);

    public static string GetContent(Project project, string fileName, string versionName)
    {
        return project.Documents
            .Where(x => x.VersionName == versionName && x.FileName == fileName)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.Content)
            .FirstOrDefault() ?? "";
    }

    /// <summary>
    /// Bản ĐÃ DUYỆT mới nhất (V lớn nhất) của một tài liệu; null khi dự án chưa duyệt lần nào hoặc bản
    /// duyệt rỗng. Khác <see cref="GetContent"/> ở chỗ không cần biết trước tên phiên bản: sau khi duyệt,
    /// <c>ApproveRequirementUseCase</c> đổi chính dòng draft thành "V{n}" nên tra theo "draft" trả rỗng.
    /// </summary>
    public static ApprovedDocument? GetLatestApproved(Project project, string fileName)
    {
        return project.Documents
            .Where(x => x.IsApproved && x.FileName == fileName && !string.IsNullOrWhiteSpace(x.Content))
            .Select(x => new { Doc = x, Number = ParseVersionNumber(x.VersionName) })
            .OrderByDescending(x => x.Number)
            .ThenByDescending(x => x.Doc.CreatedAt)
            .Select(x => new ApprovedDocument(x.Doc.VersionName, x.Doc.Content))
            .FirstOrDefault();
    }

    // "V3" → 3. Cùng cách đọc với ApproveRequirementUseCase khi tính phiên bản kế tiếp; tên lạ ⇒ 0 (vẫn
    // xét, chỉ xếp sau các phiên bản đọc được số).
    private static int ParseVersionNumber(string versionName)
        => int.TryParse(versionName.Replace("V", ""), out var n) ? n : 0;
}
