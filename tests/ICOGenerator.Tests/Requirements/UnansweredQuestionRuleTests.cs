using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// "HỎI LẠI" LÀ XIN LẠI THÔNG TIN NGƯỜI DÙNG ĐÃ ĐƯA — không phải đặt lại một câu đã từng đặt.
//
// Ca thật (dự án Learning & Development, AI Call Logs 2026-11-25): BA hỏi "HR đang dùng công cụ gì để lên kế
// hoạch mở lớp?", người dùng đáp "File Excel, Email", BA xin đính kèm đúng file Excel đó — và lời xin bị phanh
// chống hỏi lại thay bằng một câu của nhóm khác. Trọng tài chỉ nhận danh sách câu đã hỏi, không nhận câu
// người dùng đáp, nên nó không có cách nào thấy người dùng chưa hề gửi file. Code nay gửi sổ kèm câu đáp
// (InterviewJudgeTests, BAChatRepeatedQuestionTests); các test dưới đây ghim nửa còn lại — luật bằng chữ mà
// hai prompt dạy, vì code chỉ thi hành phán quyết của trọng tài, còn phán đúng hay sai là do prompt.
public class UnansweredQuestionRuleTests
{
    [Fact]
    public void TheTurnJudge_DefinesARepeatByWhatTheUserAlreadySaid()
    {
        var judge = PromptFixture.Read("BusinessAnalyst/turn-judge.v1.md");

        // Đầu vào là sổ THEO LƯỢT kèm câu đáp — đúng tiêu đề InterviewJudge.RenderRepeatInput in ra.
        Assert.Contains("`## Các lượt BA đã hỏi và câu người dùng đáp`", judge, StringComparison.Ordinal);
        Assert.Contains("`(chưa đáp)`", judge, StringComparison.Ordinal);
        Assert.Contains("`[đính kèm: …]`", judge, StringComparison.Ordinal);

        Assert.Contains("**Hỏi lại là XIN LẠI THÔNG TIN NGƯỜI DÙNG ĐÃ ĐƯA**", judge, StringComparison.Ordinal);
        Assert.Contains("**Câu người dùng đã BỎ QUA**", judge, StringComparison.Ordinal);
        Assert.Contains("**đã được hỏi từ hai lần trở lên**", judge, StringComparison.Ordinal);
        Assert.Contains("**Xin người dùng GỬI thứ họ vừa nhắc tới**", judge, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJudgeInputHeading_MatchesWhatThePromptDescribes()
    {
        var input = InterviewJudge.RenderRepeatInput(
            new[] { new AskedQuestionHistory.AskedTurn(new[] { "câu?" }, Array.Empty<string>()) },
            new[] { new InterviewJudge.TurnQuestion("câu mới?", Array.Empty<string>()) },
            summaryCheck: false);

        Assert.StartsWith("## Các lượt BA đã hỏi và câu người dùng đáp", input, StringComparison.Ordinal);
        Assert.Contains("→ Người dùng: (chưa đáp)", input, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChatPrompt_LetsBaReAskAnUnansweredQuestionOnce()
    {
        var chat = PromptFixture.Read("BusinessAnalyst/requirement-chat.v4.md");

        Assert.Contains("## CÂU NGƯỜI DÙNG CHƯA TRẢ LỜI", chat, StringComparison.Ordinal);
        Assert.Contains("**Hỏi lại MỘT lần**", chat, StringComparison.Ordinal);
        Assert.Contains("đừng hỏi lần ba", chat, StringComparison.Ordinal);
        // Lời mời đính kèm chung chung đầu buổi không thay cho lời xin đích danh file người dùng vừa nhắc.
        Assert.Contains("**không thay** cho lượt này", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSignalJudge_CountsAShortToolAnswerAsASourceMention()
    {
        var signal = PromptFixture.Read("BusinessAnalyst/turn-signal-judge.v1.md");

        Assert.Contains("*\"File Excel, Email\"* ⇒ `true`", signal, StringComparison.Ordinal);
    }
}
