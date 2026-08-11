using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Vòng lặp câu hỏi chết — ca thật đã gặp trên màn hình, ba lượt liên tiếp giống hệt nhau:
//
//   BA:          "Trước khi viết tài liệu, mình cần làm rõ thêm nhóm thông tin «Dữ liệu / danh mục
//                 chính». Trước tiên về «Dữ liệu / danh mục chính» (…) — bạn chia sẻ giúp mình nhé."
//   Người dùng:  "mình không hiểu câu hỏi của bạn, hãy giải thích rõ hơn"
//
// Hai khiếm khuyết độc lập chồng lên nhau, và test này giữ cả hai:
//
// 1. NGUỒN: dòng «Dữ liệu / danh mục chính» kẹt [MỘT PHẦN] với "còn thiếu: chốt bộ cột chính thức" trong
//    khi người dùng đã chốt bộ cột đó bằng BẢNG CỘT từ lượt thứ ba. Lượt distill bản đồ không hề được
//    đưa bảng cột — SourceContextBuilder gắn nó cho lượt chat, còn RequirementCoverageService thì chỉ gửi
//    ExtractedText. Bằng chứng nằm ngay trong DB mà "giám khảo" không được nhìn.
// 2. TRIỆU CHỨNG: cổng readiness thay lời mời "Write Requirement" của BA bằng một câu dựng sẵn kết thúc
//    bằng "bạn chia sẻ giúp mình nhé" và gọi nhóm bằng NHÃN NỘI BỘ của bản đồ. Người dùng nghiệp vụ
//    không có cách nào trả lời câu đó, nên vòng lặp không tự thoát được.
public class CoverageDeadQuestionLoopTests
{
    // Bảng cột đã chốt là câu trả lời của người dùng, chỉ khác là họ trả lời bằng cách tích từng dòng.
    // Distiller phải nhìn thấy nó, nếu không dòng bản đồ không bao giờ lên [RÕ] được.
    [Fact]
    public void ConfirmedColumnTable_IsRenderedForTheDistiller()
    {
        const string columnMap = """
            [
              { "column": "Global ID", "meaning": "Mã định danh nhân viên", "used": true },
              { "column": "Revision Number", "meaning": "Số phiên bản hệ cũ", "used": false }
            ]
            """;

        var block = SourceColumnMapBuilder.RenderConfirmedBlock("LearningPlanTemplate.xlsx", columnMap);

        Assert.NotNull(block);
        Assert.Contains("đã được NGƯỜI DÙNG CHỐT", block, StringComparison.Ordinal);
        Assert.Contains("Global ID", block, StringComparison.Ordinal);
        Assert.Contains("Revision Number", block, StringComparison.Ordinal);
    }

    // Câu hỏi dựng sẵn phải hỏi ĐÚNG phần "còn thiếu: …" — thứ duy nhất bước soạn tài liệu còn phải tự
    // đoán — chứ không đọc lại nhãn nhóm và cả tóm tắt máy.
    [Fact]
    public void PendingQuestion_AsksTheMissingPart_NotTheInternalGroupLabel()
    {
        var readiness = RequirementReadinessGate.Evaluate("""
            - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch lớp học. {nguồn: "lên kế hoạch các lớp học"}
            - Dữ liệu / danh mục chính: [MỘT PHẦN] Master List gồm 6 cột đã chốt; còn thiếu: ai quản lý danh mục khóa học của ứng dụng.
            """);

        Assert.False(readiness.Ready);

        // Hỏi đúng mẩu còn hụt…
        Assert.Contains("ai quản lý danh mục khóa học của ứng dụng", readiness.Message, StringComparison.Ordinal);
        // …và là một CÂU HỎI, không phải bản tin trạng thái.
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);

        // Nhãn nhóm được phép đứng làm chủ đề để người dùng biết đang bàn phần nào…
        Assert.Contains("«Dữ liệu / danh mục chính»", readiness.Message, StringComparison.Ordinal);
        // …nhưng KHÔNG phát lại tóm tắt máy (đọc lên tưởng bị hỏi lại điều vừa trả lời), và lượt này
        // không được kết thúc bằng một lời mời trống nghĩa như bản cũ.
        Assert.DoesNotContain("Master List gồm 6 cột đã chốt", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bạn chia sẻ giúp mình nhé", readiness.Message, StringComparison.Ordinal);
    }

    // Dòng [CHƯA HỎI] không có phần "còn thiếu" nào để bám ⇒ mới được phép hỏi câu mở đầu của nhóm; kể cả
    // khi đó cũng phải kết thúc bằng dấu hỏi và hỏi theo góc nhìn công việc thật.
    [Fact]
    public void PendingQuestion_FallsBackToAnOpeningQuestion_WhenNothingWasAskedYet()
    {
        var readiness = RequirementReadinessGate.Evaluate("""
            - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch lớp học. {nguồn: "lên kế hoạch các lớp học"}
            - Thông báo / nhắc nhở: [CHƯA HỎI]
            """);

        Assert.False(readiness.Ready);
        Assert.Contains("Thông báo / nhắc nhở", readiness.Message, StringComparison.Ordinal);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
    }

    // Ghi chú tái mở "(ghi nhận trước đó: …)" là ghi chép CŨ của hệ thống dành cho BA, không phải điều
    // cần hỏi — đọc nguyên khối vào câu hỏi là kể lại chính lời người dùng rồi bắt họ nghe lại.
    [Fact]
    public void PendingQuestion_DropsTheReopenBookkeeping()
    {
        var readiness = RequirementReadinessGate.Evaluate($"""
            - ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: {AskedQuestionHistory.ReopenNote} (ghi nhận trước đó: trưởng phòng duyệt đơn)
            """);

        Assert.False(readiness.Ready);
        Assert.DoesNotContain("ghi nhận trước đó", readiness.Message, StringComparison.Ordinal);
    }

    // Bản đồ chưa có/hỏng ⇒ fail-closed, và câu chặn vẫn phải nói được cho người dùng biết làm gì tiếp.
    [Fact]
    public void EmptyMap_StaysFailClosed()
    {
        var readiness = RequirementReadinessGate.Evaluate(null);

        Assert.False(readiness.Ready);
        Assert.False(string.IsNullOrWhiteSpace(readiness.Message));
    }
}
