using Xunit;

namespace ICOGenerator.Tests.Requirements;

// CÂU VÉT NÊU ĐÍCH DANH N CA LÀ MỘT CÂU HỎI N VẾ.
//
// Ca thật (dự án quản lý khóa học bắt buộc — AI Call Log BAChat 2026-09-01, lượt 38–39). BA hỏi *"ngoài
// việc khóa học hết hạn, còn có trường hợp nào khác cần xử lý không? Ví dụ như nhân viên nghỉ việc,
// chuyển phòng ban, hay khóa học bị hủy…"*; người dùng trả lời **nghỉ việc** và **chuyển vai trò**. Hai
// ca do chính BA đặt lên bàn — *chuyển phòng ban* và *khóa học bị hủy* — không ai đụng tới, nhưng dòng
// «Luồng ngoại lệ & trường hợp đặc biệt» vẫn lên [RÕ] vì chuẩn của nhóm chỉ đòi "ít nhất một tình huống
// hỏng KÈM cách xử lý".
//
// Thiệt hại kép, và vế thứ hai mới là vế đắt: [RÕ] vừa CẤM BA hỏi lại nhóm đó, vừa xóa luôn câu hỏi còn treo —
// thứ duy nhất chỉ đường hỏi tiếp. Không còn gì hợp lệ để hỏi, BA phát lại nguyên khung câu vét với một
// danh sách ví dụ khác và đốt trọn một lượt.
//
// Phần "không phát lại" đã có phanh tất định (AskedQuestionHistory.IsSweepRepeat). Phần "đừng đóng nhóm
// khi còn ca treo" thì không máy nào suy hộ được — nó là chuẩn thẩm định, nên tầng chặn là prompt, và
// test này giữ cho nó không âm thầm rơi mất.
public class CoverageSweepQuestionRuleTests
{
    [Fact]
    public void CoveragePrompt_CountsEachNamedExampleAsAClauseOfTheQuestion()
    {
        var prompt = CoveragePromptFixture.Read();

        // Luật chung nằm ở mục "đếm vế", chỗ chủ quản của nó.
        Assert.Contains("Câu trả lời chỉ chạm được MỘT VẾ", prompt, StringComparison.Ordinal);
        Assert.Contains("nêu đích danh N ca ví dụ", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoveragePrompt_DoesNotLetTheOneScenarioFloorCloseTheExceptionGroup()
    {
        var prompt = CoveragePromptFixture.Read();

        Assert.Contains("Luồng ngoại lệ & trường hợp đặc biệt", prompt, StringComparison.Ordinal);
        // "Ít nhất một" là SÀN cho ca người dùng tự kể, không phải giấy phép đóng nhóm khi BA đã liệt kê.
        Assert.Contains("Trần một-ca không phải là chuẩn", prompt, StringComparison.OrdinalIgnoreCase);
        // Và câu hỏi phải gọi TÊN các ca còn treo — cổng readiness lấy nguyên văn nó làm câu chặn.
        Assert.Contains("khóa học bị hủy", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
