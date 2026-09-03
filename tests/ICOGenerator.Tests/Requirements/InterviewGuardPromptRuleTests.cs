using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Mỗi chốt chặn TẤT ĐỊNH của lượt chat là một GIAO ƯỚC hai chiều với prompt, và không mối nối nào ở đây
// được compiler kiểm: code chặn, prompt phải NÓI ra rằng nó bị chặn. Thiếu vế prompt thì model cứ viết
// lại đúng hình dạng bị chặn ở mọi lượt, còn người dùng thì thấy lượt của BA "bị thay" mà không ai giải
// thích được vì sao — đúng kiểu hỏng im lặng mà cả tầng chốt chặn này sinh ra để dẹp.
//
// Các luật dưới đây đến từ bản rà soát buổi phỏng vấn dự án JD Libary 5.
public class InterviewGuardPromptRuleTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";
    private const string CoveragePromptKey = "BusinessAnalyst/requirement-coverage.v4.md";

    // Nhóm ngoại lệ: hỏi MỘT MÌNH, và cặp chip có/không bị xoá. Cả hai vế đều do
    // InterviewQuestionRules cưỡng chế, nên prompt phải kê cả hai.
    [Fact]
    public void ChatPrompt_SaysTheExceptionQuestionIsAskedAloneAndWithoutYesNoChips()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("hỏi MỘT MÌNH", prompt, StringComparison.Ordinal);
        Assert.Contains("các câu đi kèm bị bỏ", prompt, StringComparison.Ordinal);
        Assert.Contains("Không có trường hợp đặc biệt", prompt, StringComparison.Ordinal);
    }

    // Nhóm báo cáo: cùng luật xoá chip, vì một tiếng "không cần" cũng đưa dòng thẳng tới [KHÔNG ÁP DỤNG].
    [Fact]
    public void ChatPrompt_SaysTheReportGroupCannotBeAskedWithAYesNoPair()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("từng báo cáo một", prompt, StringComparison.Ordinal);
        Assert.Contains("bộ chip dạng có/không của nhóm này bị **xóa sạch**", prompt, StringComparison.Ordinal);
    }

    // Lượt xin file thay TRỌN lượt — model phải biết câu hỏi nó vừa viết đi đâu, nếu không nó sẽ viết lại
    // câu đó ở lượt sau kèm một lời xin file thứ hai.
    [Fact]
    public void ChatPrompt_SaysTheSourceRequestTurnTakesOverTheWholeTurn()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("lượt trả lời của bạn bị thay bằng một lời xin file đứng một mình", prompt, StringComparison.Ordinal);
    }

    // openEnded không mua được quyền miễn trừ khỏi chốt chặn lượt câm.
    [Fact]
    public void ChatPrompt_SaysTheOpenEndedFlagIsNotAnAnswerSlot()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("`openEnded: true` KHÔNG biến một lượt không hỏi gì thành lượt có chỗ trả lời", prompt, StringComparison.Ordinal);
        Assert.Contains("không có dấu hỏi", prompt, StringComparison.Ordinal);
    }

    // Bộ chip dự phòng của lượt tóm tắt là hằng số trong code; prompt phải kê ĐÚNG bộ đó, nếu không model
    // viết một bộ khác và màn hình có hai kiểu chip cho cùng một loại lượt.
    [Fact]
    public void ChatPrompt_ListsTheSameSummaryChipsTheCodeAttaches()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        foreach (var chip in BAChatService.SummaryCheckSuggestions)
            Assert.Contains(chip, prompt, StringComparison.Ordinal);

        // …và cấm biến lượt tóm tắt thành câu hỏi về ĐỘ ĐẦY ĐỦ của buổi phỏng vấn.
        Assert.Contains("đã đầy đủ chưa", prompt, StringComparison.Ordinal);
    }

    // Bộ trường dữ liệu được chốt bằng BẢNG ĐỐI TƯỢNG, không phải bằng cách bắt người dùng liệt kê cột.
    [Fact]
    public void ChatPrompt_ForbidsAskingTheUserToEnumerateFields()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("mỗi JD cần lưu những thông tin gì?", prompt, StringComparison.Ordinal);
        Assert.Contains("BẢNG ĐỐI TƯỢNG", prompt, StringComparison.Ordinal);
    }

    // Mẩu "còn thiếu" rỗng nghĩa bị RequirementReadinessGate bỏ qua; distiller phải biết để không viết.
    [Fact]
    public void CoveragePrompt_ForbidsHollowGaps()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("các quy tắc khác (nếu có)", prompt, StringComparison.Ordinal);
        Assert.Contains("HỎI ĐƯỢC MỘT ĐIỀU CỤ THỂ", prompt, StringComparison.Ordinal);
    }

    // Trường được đổi tên `gap` → `nextQuestion` chính vì cái tên cũ mời gọi một câu MÔ TẢ CHỖ HỤT, mà cổng
    // thì phát nguyên văn nó ra màn hình. Prompt phải nói thẳng luật ấy, kèm ca thật, nếu không lần sửa sau
    // chỉ còn thấy một cái tên trường mà không biết vì sao nó là tên đó.
    [Fact]
    public void CoveragePrompt_RequiresAQuestionNotAStateReport()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("là một CÂU HỎI, không phải một câu tường thuật trạng thái", prompt, StringComparison.Ordinal);
        Assert.Contains("Bảng thông báo theo sự kiện chưa được chốt", prompt, StringComparison.Ordinal);
    }

    // Hai nhóm chốt-bằng-bảng: BA bị cấm hỏi lẻ chúng, nên một câu hỏi gắn vào đó không có đường nào được
    // trả lời. Luật này có chốt chặn tất định (CoverageQuestionGuard) nhưng vẫn phải nằm trong prompt —
    // distiller viết ra một ô sẽ bị xoá là mất trắng một lượt khai thác.
    [Fact]
    public void CoveragePrompt_KeepsTheTableDecidedGroupsQuestionless()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("Hai nhóm chốt bằng BẢNG luôn để `nextQuestion` RỖNG", prompt, StringComparison.Ordinal);
        Assert.Contains("Phân quyền theo nghiệp vụ", prompt, StringComparison.Ordinal);
        Assert.Contains("Thông báo / nhắc nhở", prompt, StringComparison.Ordinal);
    }

    // Bản đồ là NGUỒN DUY NHẤT của câu hỏi kế tiếp ⇒ danh sách tồn đọng phải là ĐẦU VÀO của lượt distill,
    // không phải một danh sách song song chỉ gặp bản đồ ở chốt chặn hậu kỳ.
    [Fact]
    public void CoveragePrompt_TellsTheDistillerHowToUseThePendingOpenQuestions()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("## Điểm cần làm rõ còn tồn đọng", prompt, StringComparison.Ordinal);
        // Khối này chắt ở hậu kỳ nên nó luôn cũ hơn bản đồ một lượt — luật quan trọng nhất của mục đó.
        Assert.Contains("luôn CŨ hơn bản đồ đúng một lượt", prompt, StringComparison.Ordinal);
    }

    // Guard ví dụ số hạ dòng quy tắc bất kể distiller chấm gì — nói ra để nó không cố "chữa" bằng cách
    // viết tóm tắt dài hơn.
    [Fact]
    public void CoveragePrompt_SaysANumericRuleNeedsAConfirmedWorkedExample()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("Ví dụ đã xác nhận", prompt, StringComparison.Ordinal);
        Assert.Contains("hạ xuống `[MỘT PHẦN]`", prompt, StringComparison.Ordinal);
    }

    private static string ReadPrompt(string promptKey)
    {
        var relative = promptKey.Replace('/', Path.DirectorySeparatorChar);

        var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts", relative);
        if (File.Exists(fromBin))
            return File.ReadAllText(fromBin);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Prompts", relative);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("Không tìm thấy prompt " + promptKey + " từ " + AppContext.BaseDirectory);
    }
}
