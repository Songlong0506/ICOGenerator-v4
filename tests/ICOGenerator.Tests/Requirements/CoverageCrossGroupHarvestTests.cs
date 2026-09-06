using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// MỘT LƯỢT TRẢ LỜI CHẠM ĐƯỢC NHIỀU NHÓM — và lượt chắt lọc phải ghi vào TẤT CẢ các nhóm nó chạm.
//
// Câu HỎI có nhãn nhóm, câu TRẢ LỜI thì không: người dùng nghiệp vụ trả lời theo công việc của họ chứ
// không theo 12 ô của bản đồ. Ca thật (dự án quản lý khóa học bắt buộc — AI Call Log 2026-09-06, lượt
// 17): BA hỏi việc của quản lý trực tiếp và HR, người dùng đáp *"quản lý trực tiếp sẽ xem được lịch sử
// học tập của những nhân viên của mình, còn HR thì xem báo cáo về tình trạng học tập của các phòng ban
// trong nhà máy"*. Cả câu vào đúng MỘT dòng «Đối tượng người dùng & vai trò»; dòng «Báo cáo / thống kê»
// ở lại [CHƯA HỎI] với `known` rỗng, dù vế sau của câu đáp chính là một báo cáo kèm sẵn người xem và
// chiều tổng hợp.
//
// Thiệt hại đến ở lượt 46 và nó không tự lộ ra ở đâu khác: [CHƯA HỎI] là lệnh cho BA phát CÂU MỞ ĐẦU của
// nhóm (requirement-chat.v4.md), nên người dùng nhận đúng *"những báo cáo nào là cần thiết cho ứng dụng
// này? Ví dụ: báo cáo tình trạng học tập của nhân viên, báo cáo theo phòng ban…"* — hỏi lại điều họ đã
// trả lời 29 lượt trước, và lấy chính câu trả lời của họ ra làm ví dụ gợi ý.
//
// Không máy nào suy hộ được "câu này thuộc những nhóm nào" — đó là chuẩn thẩm định, nên tầng chặn là
// PROMPT (hai file, hai chiều: chiều GHI ở coverage, chiều HỎI ở chat), và test này giữ cho nó không âm
// thầm rơi mất. Cùng khuôn với CoverageSweepQuestionRuleTests.
public class CoverageCrossGroupHarvestTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";
    private const string CoveragePromptKey = "BusinessAnalyst/requirement-coverage.v5.md";

    [Fact]
    public void CoveragePrompt_RecordsOneTurnIntoEveryGroupItTouches()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("MỘT LƯỢT CHẠM ĐƯỢC NHIỀU NHÓM", prompt, StringComparison.Ordinal);
        // Luật phải nói rõ VIỆC phải làm, không chỉ nêu hiện tượng: quét cả 12 nhãn cho mỗi lượt mới.
        Assert.Contains("một lượt qua CẢ 12 nhãn", prompt, StringComparison.OrdinalIgnoreCase);
        // Và phải cấp phép cho việc một điều đứng ở hai dòng — không có câu này thì luật "đừng chép sang
        // nhóm khác" của bản năng tóm tắt sẽ thắng.
        Assert.Contains("Một điều đứng ở hai dòng là **đúng**", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Bộ chắt lọc CHỈ nhìn thấy các lượt MỚI, nên một điều bị xếp sót không bao giờ được lượt hội thoại
    // của nó đưa lại lần thứ hai: đường sửa duy nhất là chính bản đồ, ở mọi lượt sau đó.
    [Fact]
    public void CoveragePrompt_RepairsAMisfiledFactFromTheMapItself()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("Sửa được HỒI TỐ", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quét `known` của 11 dòng kia", prompt, StringComparison.OrdinalIgnoreCase);

        // Ranh giới đi kèm, nếu không luật này biến thành phép nhân bản mọi phần tử sang mọi dòng.
        Assert.Contains("để việc này không thành nhân bản", prompt, StringComparison.OrdinalIgnoreCase);
        // Chạm một nhóm không phải là đóng nhóm đó — hai nhóm chốt bằng BẢNG vẫn giữ luật một chiều.
        Assert.Contains("chưa có bảng thì không bao giờ `[RÕ]`", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Chiều thứ hai của cùng một lỗ hổng: kể cả khi bản đồ đã sót, BA vẫn còn cả hội thoại trong ngữ cảnh
    // và không được phép phát câu mở đầu của một nhóm mà người dùng đã nói tới.
    [Fact]
    public void ChatPrompt_DoesNotFireTheGroupOpener_WhenTheTranscriptAlreadyAnsweredIt()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("nhưng soát hội thoại trước đã", prompt, StringComparison.OrdinalIgnoreCase);
        // [CHƯA HỎI] nói về BẢN ĐỒ, không nói về người dùng — đây là câu chốt của luật.
        Assert.Contains("KHÔNG phải người dùng chưa nói gì", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TUYỆT ĐỐI không phát câu mở đầu", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GoldenSet_ScoresTheCrossGroupHarvest_OnCoveragePrompt()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == CoveragePromptKey)
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c =>
            c.Contains("Báo cáo / thống kê", StringComparison.OrdinalIgnoreCase)
            && c.Contains("KHÔNG được để [CHƯA HỎI]", StringComparison.OrdinalIgnoreCase));
    }

    // Prompts/ được copy vào output của app và flow sang bin của test qua ProjectReference; nếu môi
    // trường build không copy transitives thì đi ngược từ BaseDirectory lên repo root.
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

        throw new FileNotFoundException("Không tìm thấy prompt " + promptKey);
    }
}
