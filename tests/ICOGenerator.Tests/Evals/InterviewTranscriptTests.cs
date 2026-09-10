using ICOGenerator.Services.Evals;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// Phần ĐO ĐƯỢC của một cuộc phỏng vấn mô phỏng — thứ mà một judge chấm 1–5 hay bỏ sót và cũng không nhất
// quán giữa các lần chạy: phỏng vấn có tới đích không, tốn mấy lượt, và có tự phá các quy tắc mà chính
// prompt đặt ra (mỗi lượt một câu hỏi, hỏi thì phải kèm gợi ý) không.
public class InterviewTranscriptTests
{
    private static InterviewTurn Turn(string ba, string user, params string[] suggestions) =>
        new(ba, suggestions, user);

    // Lượt BA khai `ready` — tức MỜI bấm nút. Đây là thứ InterviewTranscript đọc để nói "phỏng vấn tới
    // đích": cờ của lượt, KHÔNG phải mặt chữ của lời thoại (một kịch bản nhắc tên nút mà chưa mời gì thì
    // không phải đã tới đích).
    private static InterviewTurn InviteTurn(string ba, string user) =>
        new(ba, Array.Empty<string>(), user, Invites: true);

    [Fact]
    public void Measure_CountsTurnsAndDetectsReachingTheGate()
    {
        var turns = new[]
        {
            Turn("Ai là người dùng chính?", "Nhân viên và quản lý", "Nhân viên", "Quản lý"),
            InviteTurn("Mình đã nắm đủ thông tin. Anh/chị bấm nút \"Write Requirement\" để tạo tài liệu nhé.", "ok bạn")
        };

        var metrics = InterviewTranscript.Measure(turns);

        Assert.Equal(2, metrics.Turns);
        Assert.True(metrics.ReachedWriteRequirement);
        Assert.Equal(0, metrics.TurnsWithMultipleQuestions);
    }

    [Fact]
    public void Measure_UnfinishedInterview_IsNotReachedTheGate()
    {
        var turns = new[] { Turn("Quy trình hiện tại thế nào?", "Ghi sổ tay", "Ghi sổ") };

        Assert.False(InterviewTranscript.Measure(turns).ReachedWriteRequirement);
    }

    // NHẮC TÊN NÚT KHÔNG PHẢI LÀ MỜI. Hồi phép đo còn dò chuỗi trong lời thoại, đúng lượt BA giải thích
    // rằng CHƯA nên bấm nút lại được tính là "phỏng vấn tới đích" — và eval dừng ngay tại đó.
    [Fact]
    public void Measure_MentioningTheButtonWithoutInviting_IsNotReachedTheGate()
    {
        var turns = new[]
        {
            Turn("Mình chưa mở nút \"Write Requirement\" vì còn vài điểm chưa rõ. Ai duyệt đơn ạ?", "Quản lý", "Quản lý")
        };

        Assert.False(InterviewTranscript.Measure(turns).ReachedWriteRequirement);
    }

    [Fact]
    public void Measure_FlagsTurnsThatCramMultipleQuestions()
    {
        var turns = new[]
        {
            Turn("Ai duyệt đơn? Và duyệt mấy cấp?", "Quản lý, một cấp", "Một cấp"),
            Turn("Nghỉ nửa ngày tính sao?", "0,5 ngày", "0,5 ngày")
        };

        Assert.Equal(1, InterviewTranscript.Measure(turns).TurnsWithMultipleQuestions);
    }

    [Fact]
    public void Measure_FlagsQuestionsWithoutSuggestions()
    {
        var turns = new[]
        {
            Turn("Ai duyệt đơn?", "Quản lý"),                       // hỏi mà không kèm gợi ý
            Turn("Bao nhiêu người dùng?", "120", "Dưới 50", "Trên 100"),
            Turn("Đã ghi nhận.", "ok")                               // không hỏi ⇒ không tính
        };

        Assert.Equal(1, InterviewTranscript.Measure(turns).QuestionTurnsWithoutSuggestions);
    }

    [Fact]
    public void Measure_EmptyInterview_IsNotReached()
    {
        var metrics = InterviewTranscript.Measure(Array.Empty<InterviewTurn>());

        Assert.Equal(0, metrics.Turns);
        Assert.False(metrics.ReachedWriteRequirement);
    }

    [Fact]
    public void Render_ShowsBothSidesAndSuggestions()
    {
        var text = InterviewTranscript.Render(new[] { Turn("Ai dùng?", "Nhân viên", "Nhân viên", "Quản lý") });

        Assert.Contains("**BA (lượt 1):** Ai dùng?", text);
        Assert.Contains("Nhân viên · Quản lý", text);
        Assert.Contains("**Người dùng:** Nhân viên", text);
    }

    [Fact]
    public void Format_SaysWhetherInterviewReachedTheGate()
    {
        var reached = InterviewTranscript.Measure(new[]
        {
            InviteTurn("Xong rồi, anh/chị bấm \"Write Requirement\" giúp mình nhé.", "ok")
        }).Format();
        var unfinished = InterviewTranscript.Measure(new[] { Turn("Còn câu này nữa?", "ừ", "ừ") }).Format();

        Assert.Contains("phỏng vấn tới đích", reached);
        Assert.Contains("CHẠM TRẦN", unfinished);
    }
}
