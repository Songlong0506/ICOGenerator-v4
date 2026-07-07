namespace ICOGenerator.Services.Quality;

// Ba record nhỏ liên quan chặt của MỘT cấu trúc dữ liệu (ma trận truy vết) — để chung file theo ngoại lệ
// "DTO nhóm nhỏ liên quan chặt". Đây vừa là hình dạng JSON model phải trả (Quality/traceability-matrix.v1.md)
// vừa là hình dạng lưu trong ProjectTraceability.MatrixJson (serialize lại sau khi parse — camelCase).

/// <summary>Một dòng ma trận: một yêu cầu và các dấu vết của nó ở hạ nguồn (story / code / test).</summary>
public sealed record TraceabilityRequirement(
    string Code,
    string Title,
    string? Kind,
    IReadOnlyList<string> Stories,
    IReadOnlyList<string> CodeFiles,
    IReadOnlyList<string> Tests,
    string Status,
    string? Note);

/// <summary>User story không truy vết được về yêu cầu nào — tín hiệu story tự thêm ngoài yêu cầu.</summary>
public sealed record TraceabilityOrphanStory(string Story, string? Reason);

/// <summary>Ma trận truy vết đầy đủ của một dự án.</summary>
public sealed record TraceabilityMatrix(
    IReadOnlyList<TraceabilityRequirement> Requirements,
    IReadOnlyList<TraceabilityOrphanStory> OrphanStories,
    string? Summary)
{
    public const string StatusCovered = "covered";
    public const string StatusPartial = "partial";
    public const string StatusMissing = "missing";
}
