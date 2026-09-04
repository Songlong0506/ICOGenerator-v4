using Xunit;

namespace ICOGenerator.Tests.Requirements;

// VÒNG LẶP KẸT CUỐI BUỔI PHỎNG VẤN — ba luật prompt cùng giữ một chốt chặn, và không luật nào có mối nối
// mà compiler kiểm được.
//
// Ca thật (dự án JD Libary 5): một dòng bản đồ kẹt [MỘT PHẦN] với mẩu *"còn thiếu: chức năng «Xác nhận đã
// ký đủ» CHỈ nằm ở trang A HAY CHỈ nằm ở trang B"*, trong khi người dùng đã trả lời *"trên cả hai trang"* —
// một câu trả lời hợp lệ nhưng không nằm trong hai nhánh mà chính mẩu đó nêu ra. Dòng không bao giờ đóng
// được, cổng "Write Requirement" không bao giờ mở, BA thì bị cấm hỏi lại điều vừa được trả lời, nên bốn
// lượt cuối của buổi phỏng vấn 90 lượt trôi qua bằng *"mình tiếp tục bước rà soát cuối"* — một bước không
// tồn tại. Cùng buổi đó, một điểm tồn đọng đã được người dùng bấm "Đồng ý" từ 15 lượt trước vẫn nằm trong
// danh sách tồn đọng, nên CoveragePendingGuard hạ lại dòng bản đồ tương ứng ở mọi lượt.
//
// Chốt chặn tất định của lượt chat (BAChatSilentTurnTests) chỉ chữa TRIỆU CHỨNG — nó thay lượt câm bằng
// một câu hỏi trả lời được. Ba luật dưới đây chữa NGUYÊN NHÂN; thiếu chúng thì câu hỏi mà cổng phát ra
// vẫn là câu người dùng đã trả lời rồi.
public class InterviewDeadEndRuleTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";
    private const string CoveragePromptKey = "BusinessAnalyst/requirement-coverage.v5.md";
    private const string OutlookPromptKey = "BusinessAnalyst/interview-outlook.v3.md";

    // Prompt chat từng TỰ CHO PHÉP lượt câm: luật về `suggestions` liệt kê "lượt hoàn toàn KHÔNG cần người
    // dùng trả lời (`ready: true`, hoặc chỉ thông báo đã xong)" — vế cuối là đúng cái lối thoát mà model
    // lách vào khi nó không còn gì hỏi mà cũng chưa được mời bấm nút.
    [Fact]
    public void ChatPrompt_NoLongerAuthorisesAStatementOnlyTurn()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.DoesNotContain("chỉ thông báo đã xong", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatPrompt_RequiresEveryTurnToCarryAnAnswerSlot()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("MỌI LƯỢT PHẢI CÓ CHỖ TRẢ LỜI", prompt, StringComparison.Ordinal);

        // Và cấm đúng hình dạng đã gặp trên màn hình: kết lượt bằng lời hứa về một "bước" của BA. Ở chế độ
        // chat không có bước nào chạy giữa hai lượt — việc duy nhất còn lại là người dùng bấm nút.
        Assert.Contains("mình tiếp tục bước rà soát cuối", prompt, StringComparison.Ordinal);
    }

    // Luật "tin HỘI THOẠI, đừng hỏi lại" (dành cho bản đồ chậm một lượt) là thứ đẩy BA vào chỗ im lặng khi
    // nó tin rằng mọi thứ đã được trả lời. Nó phải tự nói rõ "đi tiếp" nghĩa là gì.
    [Fact]
    public void ChatPrompt_SaysTrustingTheTranscriptIsNotAnExcuseForSilence()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        var line = prompt
            .Split('\n')
            .FirstOrDefault(l => l.Contains("tin HỘI THOẠI và đi tiếp", StringComparison.Ordinal));

        Assert.NotNull(line);
        Assert.Contains("không bao giờ có nghĩa là im lặng", line!, StringComparison.Ordinal);
    }

    // Mẩu sau "còn thiếu:" KHÔNG phải ghi chú nội bộ: RequirementReadinessGate lấy nguyên văn nó làm câu
    // hỏi bày ra màn hình. Một mẩu viết dạng loại trừ ("chỉ A hay chỉ B") là câu hỏi mà một câu trả lời
    // hợp lệ ("cả hai") không đóng lại được — vòng lặp kín, và không tầng nào phía sau báo động.
    [Fact]
    public void CoveragePrompt_ForbidsAGapThatNoAnswerCanClose()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("ĐÓNG LẠI ĐƯỢC", prompt, StringComparison.Ordinal);
        Assert.Contains("KHÔNG viết dạng loại trừ", prompt, StringComparison.Ordinal);
    }

    // Nhóm tự mâu thuẫn — `known` đã chở câu trả lời mà một câu hỏi của nhóm vẫn hỏi đúng điều đó — là hình
    // dạng thật đã gặp, và distiller là tầng DUY NHẤT gỡ được nó (guard chỉ chạy một chiều, không nâng cấp).
    [Fact]
    public void CoveragePrompt_TellsTheDistillerToDropAGapItsOwnSummaryAlreadyAnswers()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("`known` đã chứa câu trả lời thì câu hỏi của nhóm phải ĐÓNG", prompt, StringComparison.Ordinal);
    }

    // Danh sách câu hỏi lái CoveragePendingGuard hạ dòng bản đồ, nên một mục giữ lại quá hạn khoá cổng
    // chắc chắn như một dòng [MỘT PHẦN] thật. Người dùng nghiệp vụ chốt bằng cách BẤM CHIP nhiều hơn là gõ
    // lại bằng lời của mình — không tính cái gật đó là câu trả lời thì mục nằm lại vĩnh viễn.
    [Fact]
    public void CoveragePrompt_CountsAChipAgreementAsAResolvedItem()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("BA đề xuất một phương án + người dùng gật = ĐÃ CHỐT", prompt, StringComparison.Ordinal);
        Assert.Contains("Đúng rồi, tiếp tục", prompt, StringComparison.Ordinal);
    }

    // Cùng cách tìm Prompts/ như CoverageReopenNoteRuleTests: ưu tiên bản copy trong bin, không có thì đi
    // ngược lên repo root.
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
