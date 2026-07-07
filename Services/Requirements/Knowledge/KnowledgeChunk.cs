namespace ICOGenerator.Services.Requirements.Knowledge;

/// <summary>
/// Một đoạn trích từ tài liệu ĐÃ DUYỆT của một dự án — đơn vị truy xuất của tri thức xuyên dự án.
/// Mang đủ metadata nguồn gốc (dự án, loại tài liệu, heading) để khối ngữ cảnh render cho BA nêu
/// được "trích từ đâu" và để boost các dự án cùng đơn vị yêu cầu lúc chấm điểm.
/// </summary>
public sealed record KnowledgeChunk(
    Guid ProjectId,
    string ProjectName,
    string? OrgUnitCode,
    string DocumentLabel,
    string? Heading,
    string Text);
