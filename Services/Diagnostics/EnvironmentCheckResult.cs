namespace ICOGenerator.Services.Diagnostics;

/// <summary>
/// Một dòng của bảng "Test Environment".
/// </summary>
/// <param name="Group">Nhóm hiển thị (Database, POC browser…) — mỗi <see cref="IEnvironmentCheck"/> một nhóm.</param>
/// <param name="Name">Tên ngắn của thứ được kiểm.</param>
/// <param name="Status">Kết luận.</param>
/// <param name="Message">Câu chính: thấy gì.</param>
/// <param name="Fix">Cách sửa — chỉ có khi không đạt. Viết cho người vận hành, đủ cụ thể để làm theo ngay.</param>
/// <param name="Detail">Nguyên văn kỹ thuật (thông điệp exception, output lệnh) để soi thêm; có thể null.</param>
public sealed record EnvironmentCheckResult(
    string Group,
    string Name,
    EnvironmentCheckStatus Status,
    string Message,
    string? Fix = null,
    string? Detail = null);
