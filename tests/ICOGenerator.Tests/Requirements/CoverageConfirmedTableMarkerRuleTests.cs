using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cụm bằng chứng của nhóm thông báo (dự án cũ đã chốt bảng thông báo) cũng là giao ước prompt ↔ code, cùng
// hình dạng với AskedQuestionHistory.ReopenNote.
//
// CoverageConfirmedTableGuard ghi cụm này vào `known` của dòng lúc bảng được chốt; requirement-coverage.v5.md
// dạy lượt chắt lọc kế tiếp viết lại ĐÚNG cụm ấy khi nó thấy khối "đã chốt" trong đầu vào. Hai bên lệch
// nhau thì lượt distill ghi một cụm khác, dòng mất dấu bằng chứng bảng — và vì nhóm này BỊ CẤM hỏi bằng câu
// hỏi, mất dấu là không còn đường nào đưa nó lên [RÕ] nữa.
public class CoverageConfirmedTableMarkerRuleTests
{
    [Fact]
    public void CoveragePromptWritesTheExactEvidenceMarkersTheGuardWrites()
    {
        var prompt = CoveragePromptFixture.Read();

        Assert.Contains(CoverageConfirmedTableGuard.NotificationEvidence, prompt, StringComparison.Ordinal);
    }
}
