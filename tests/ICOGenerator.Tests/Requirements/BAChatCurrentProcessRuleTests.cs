using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// QUY TRÌNH HIỆN TẠI KỂ XONG ⇒ ĐI TIẾP SANG HƯỚNG CẢI TIẾN.
//
// Ca thật (dự án JD Libary, lượt 3–6): người dùng kể xong cách làm bằng 2 file Excel, BA phát lại nguyên
// văn câu hỏi cũ, người dùng đáp "mình nói ở trên rồi đó", BA lại xin xác nhận đúng chuỗi thao tác đó
// thêm một lượt nữa. Ba lượt bị đốt, và hai thứ đắt nhất của chặng này — ĐIỂM ĐAU của cách làm hiện tại
// và MONG MUỐN CẢI TIẾN ở ứng dụng mới — không bao giờ được hỏi tới. Cái gì không được hỏi thì vắng mặt
// ở mọi tầng phía sau: tài liệu yêu cầu, bản kỹ thuật, POC.
//
// Phần "không hỏi lại" đã có phanh tất định (AskedQuestionHistory). Phần "đi tiếp sang chặng nào" thì
// không máy nào suy hộ được — nó là luật phỏng vấn, nên tầng chặn là prompt + bản đồ bao phủ + golden
// set, và test này giữ cho cả ba không âm thầm rơi mất.
public class BAChatCurrentProcessRuleTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    [Fact]
    public void ChatPrompt_OrdersTheThreeStages_AndBansReAskingTheCurrentProcess()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("HƯỚNG CẢI TIẾN", prompt, StringComparison.Ordinal);
        // Chặng 3 phải hỏi Ý TƯỞNG của người dùng trước…
        Assert.Contains("hình dung", prompt, StringComparison.OrdinalIgnoreCase);
        // …và khi họ chưa nghĩ ra thì quay về ĐIỂM ĐAU rồi tự đề xuất một quy trình cải tiến để xin chốt.
        Assert.Contains("chưa nghĩ ra", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("QUY TRÌNH CẢI TIẾN", prompt, StringComparison.OrdinalIgnoreCase);
        // Ca thật phải nằm trong prompt: đó là thứ dạy model nhận ra chính mình đang lặp.
        Assert.Contains("mình nói ở trên rồi đó", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Bản đồ bao phủ là NGUỒN CHÂN LÝ của cổng "Write Requirement": nếu chuẩn [RÕ] của dòng này không đòi
    // hướng cải tiến thì dòng lên [RÕ] ngay khi người dùng kể xong cách làm cũ, và BA bị CẤM quay lại
    // nhóm đã [RÕ] — mong muốn cải tiến vĩnh viễn không được hỏi.
    [Fact]
    public void CoveragePrompt_RequiresTheImprovementDirection_BeforeTheCurrentProcessLineIsClear()
    {
        var prompt = CoveragePromptFixture.Read();

        Assert.Contains("Quy trình hiện tại & điểm khó", prompt, StringComparison.Ordinal);
        Assert.Contains("hướng cải tiến", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GoldenSet_ScoresTheMoveToTheImprovementStage()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == ChatPromptKey)
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c =>
            c.Contains("KHÔNG hỏi lại quy trình hiện tại", StringComparison.OrdinalIgnoreCase)
            && c.Contains("MONG MUỐN CẢI TIẾN", StringComparison.OrdinalIgnoreCase));
    }

    // Cùng cách tìm Prompts/ như BAChatPlaybackRuleTests: ưu tiên bản copy trong bin, không có thì đi
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
