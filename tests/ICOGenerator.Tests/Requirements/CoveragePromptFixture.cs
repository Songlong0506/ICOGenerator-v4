using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Tests.Requirements;

/// <summary>
/// Đọc file prompt coverage THẬT từ đĩa. Nhiều test chốt rằng code khớp với file prompt (danh sách nhóm,
/// câu mở đầu của từng nhóm) — dùng chung một hàm đọc để chúng không thể trỏ vào hai bản khác nhau.
/// </summary>
internal static class CoveragePromptFixture
{
    public static string Read() => PromptFixture.Read(CoverageChecklist.CoveragePromptPath);
}
