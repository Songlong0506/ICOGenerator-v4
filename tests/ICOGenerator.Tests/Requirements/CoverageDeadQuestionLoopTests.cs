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
    // cần hỏi — đọc nguyên khối vào câu hỏi là kể lại chính lời người dùng rồi bắt họ nghe lại. Cụm
    // ReopenNote đứng ngay trước nó cũng vậy, và còn tệ hơn: nó là TÍN HIỆU MÁY (mở phanh chống-hỏi-lại)
    // nên đọc lên là một lượt hỏi rỗng nghĩa, xưng "người dùng" ở ngôi thứ ba với chính người đang đọc.
    // Ca thật: dự án JD Library lượt 34 — "Anh/chị cho mình hỏi thêm: người dùng báo phần này chưa đúng
    // — cần hỏi lại và chốt lại — anh/chị cho mình xin thông tin này nhé?".
    [Fact]
    public void PendingQuestion_DropsTheReopenBookkeeping()
    {
        var readiness = RequirementReadinessGate.Evaluate($"""
            - ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: {AskedQuestionHistory.ReopenNote} — cần hỏi lại và chốt lại. (ghi nhận trước đó: trưởng phòng duyệt đơn)
            """);

        Assert.False(readiness.Ready);
        Assert.DoesNotContain("ghi nhận trước đó", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AskedQuestionHistory.ReopenNote, readiness.Message, StringComparison.OrdinalIgnoreCase);
        // Không còn mẩu nào để hỏi ⇒ rơi về câu mở đầu của nhóm: một câu hỏi rộng vẫn trả lời được, còn
        // cụm tín hiệu thì không.
        Assert.Contains("Đối tượng người dùng & vai trò", readiness.Message, StringComparison.Ordinal);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
    }

    // Distiller viết ĐÚNG theo prompt: cụm tín hiệu, rồi mẩu còn phải hỏi, rồi ghi chép cũ trong ngoặc.
    // Câu hỏi phải lấy mảnh GIỮA — đó là thứ duy nhất người dùng trả lời được.
    [Fact]
    public void PendingQuestion_AsksTheGapWrittenAfterTheReopenMarker()
    {
        var readiness = RequirementReadinessGate.Evaluate($"""
            - ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] còn thiếu: {AskedQuestionHistory.ReopenNote} — cần hỏi lại và chốt lại. MyJD có nằm trong phạm vi màn hình không. (ghi nhận trước đó: phạm vi không có MyJD)
            """);

        Assert.False(readiness.Ready);
        Assert.Contains("MyJD có nằm trong phạm vi màn hình không", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AskedQuestionHistory.ReopenNote, readiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ghi nhận trước đó", readiness.Message, StringComparison.Ordinal);
    }

    // Lượt chặn của cổng không có chip nào (Evaluate không dựng phương án), nên nó PHẢI là câu mở — nếu
    // không, người dùng nhận một câu hỏi vừa không có nút bấm vừa không mời gõ.
    [Theory]
    [InlineData("- ★ Mục tiêu / bài toán: [MỘT PHẦN] còn thiếu: ứng dụng giải quyết việc gì.")]
    [InlineData("- Thông báo / nhắc nhở: [CHƯA HỎI]")]
    [InlineData(null)]
    public void PendingTurn_IsAlwaysAnOpenQuestion(string? map)
    {
        var readiness = RequirementReadinessGate.Evaluate(map);

        Assert.False(readiness.Ready);
        Assert.True(readiness.OpenEnded);
    }

    // Bản đồ chưa có/hỏng ⇒ fail-closed, và câu chặn vẫn phải nói được cho người dùng biết làm gì tiếp.
    [Fact]
    public void EmptyMap_StaysFailClosed()
    {
        var readiness = RequirementReadinessGate.Evaluate(null);

        Assert.False(readiness.Ready);
        Assert.False(string.IsNullOrWhiteSpace(readiness.Message));
    }

    // ==== NHÁNH DỰ PHÒNG: không nhóm nào được rơi vào một lượt trống nghĩa ====
    //
    // Ca thật thứ hai của cùng lớp lỗi — dự án JD Library, lượt 76. Người dùng vừa trả lời xong người nhận
    // của một sự kiện thông báo; BA mời bấm "Write Requirement" quá sớm; cổng thay lời mời bằng câu dựng
    // sẵn, và vì dòng «Thông báo / nhắc nhở» lúc đó không có cụm "còn thiếu:" nào, câu phát ra là
    // *"…(nhóm «Thông báo / nhắc nhở»). Anh/chị kể giúp mình phần này trong công việc thực tế hiện đang diễn
    // ra thế nào?"*. Người dùng đáp *"mình chưa hiểu câu hỏi, hãy hỏi rõ hơn"*.
    //
    // Nhánh đó reachable với BẤT KỲ nhóm nào: cụm "còn thiếu:" là định dạng do LLM xuất, không phải bất
    // biến của code.

    // Dòng [CHƯA HỎI] ⇒ câu mở đầu THẬT của nhóm, không phải câu dùng chung cho cả 12 nhóm.
    [Fact]
    public void PendingQuestion_AsksTheGroupsOwnOpeningQuestion_WhenNothingWasAskedYet()
    {
        var readiness = RequirementReadinessGate.Evaluate("""
            - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch lớp học. {nguồn: "lên kế hoạch các lớp học"}
            - Quy mô sử dụng: [CHƯA HỎI]
            """);

        Assert.Contains(CoverageGroupOpeners.Find("Quy mô sử dụng")!, readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("phần này", readiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Dòng [MỘT PHẦN] mà distiller không viết được mẩu còn hụt ⇒ PHÁT LẠI phần đã ghi nhận rồi hỏi còn
    // thiếu gì. KHÔNG được phát lại câu mở đầu của nhóm ở ca này: người dùng đã kể phần đó rồi, nghe lại
    // đúng câu cũ là mất lòng tin vào cả buổi phỏng vấn (prompt chat cấm tuyệt đối).
    [Fact]
    public void PendingQuestion_PlaysBackWhatWasRecorded_WhenTheDistillerWroteNoGap()
    {
        var readiness = RequirementReadinessGate.Evaluate("""
            - Thông báo / nhắc nhở: [MỘT PHẦN] Đã chốt To HOD của đơn vị, CC người tạo khi JD chờ HRBP verify.
            """);

        Assert.False(readiness.Ready);
        Assert.Contains("Đã chốt To HOD của đơn vị, CC người tạo khi JD chờ HRBP verify", readiness.Message, StringComparison.Ordinal);
        Assert.Contains("còn chỗ nào chưa đúng hoặc còn thiếu", readiness.Message, StringComparison.Ordinal);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
        Assert.DoesNotContain(CoverageGroupOpeners.Find("Thông báo / nhắc nhở")!, readiness.Message, StringComparison.Ordinal);
    }

    // Phần phát lại phải sạch ghi chú MÁY: cụm ReopenNote và "(ghi nhận trước đó: …)" là ghi chép của hệ
    // thống dành cho BA, đọc lên là xưng "người dùng" ở ngôi thứ ba với chính người đang đọc.
    [Fact]
    public void PlaybackDropsTheMachineBookkeeping()
    {
        var readiness = RequirementReadinessGate.Evaluate($"""
            - Vòng đời & trạng thái: [MỘT PHẦN] Đơn đi qua Chờ duyệt và Đã duyệt. {AskedQuestionHistory.ReopenNote} (ghi nhận trước đó: chỉ có hai trạng thái)
            """);

        Assert.Contains("Đơn đi qua Chờ duyệt và Đã duyệt", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ghi nhận trước đó", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AskedQuestionHistory.ReopenNote, readiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Không nhóm nào của bản đồ THẬT còn phát ra câu dùng chung — cả ở [CHƯA HỎI] lẫn [MỘT PHẦN] rỗng ruột
    // (dòng chỉ còn ghi chú máy, đã bị lược sạch trước khi phát lại).
    [Theory]
    [InlineData("[CHƯA HỎI]")]
    [InlineData("[MỘT PHẦN]")]
    public void NoRealGroupFallsBackToTheSharedSentence(string status)
    {
        foreach (var group in CoverageChecklist.Parse(CoveragePromptFixture.Read()))
        {
            var readiness = RequirementReadinessGate.Evaluate($"- {group.Label}: {status}");

            Assert.False(readiness.Ready);
            Assert.Contains(CoverageGroupOpeners.Find(group.Label)!, readiness.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("phần này", readiness.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Nhãn model tự nghĩ ra: không có câu mở đầu nào, và cổng KHÔNG được bịa một câu hỏi về một nhóm không
    // có trong checklist. Câu dẫn vẫn chở nhãn nên lượt đó còn chủ đề để bám.
    [Fact]
    public void AnUnknownGroupLabelStillGetsAnAnswerableTurn()
    {
        var readiness = RequirementReadinessGate.Evaluate("- Tích hợp hệ thống ngoài: [CHƯA HỎI]");

        Assert.False(readiness.Ready);
        Assert.Contains("«Tích hợp hệ thống ngoài»", readiness.Message, StringComparison.Ordinal);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
    }
}
