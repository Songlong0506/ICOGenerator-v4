namespace ICOGenerator.Services.Requirements.Knowledge;

/// <summary>
/// Một dự án KHÁC có tài liệu đã duyệt khớp nội dung dự án đang xét — kết quả của
/// <see cref="ProjectKnowledgeService.FindSimilarProjectsAsync"/> cho panel "Dự án tương tự".
/// Score là tổng điểm BM25 của các đoạn khớp (chỉ dùng để XẾP HẠNG, không phải phần trăm).
/// </summary>
public sealed record SimilarProject(
    Guid ProjectId,
    string ProjectName,
    string? OrgUnitCode,
    double Score,
    IReadOnlyList<string> MatchedDocuments,
    string Snippet);
