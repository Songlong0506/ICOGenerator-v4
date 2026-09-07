using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// SỐ người dùng của một ứng dụng trong nhà máy KHÔNG phải một câu hỏi — nó là hệ quả của PHẠM VI, và con số
// thì hệ thống đã cầm sẵn: OrganizationContextService render "Bối cảnh tổ chức Bosch" kèm tổng số nhân sự
// internal đang hoạt động và số nhân sự của từng department, rồi đính vào MỌI lời gọi BA.
//
// Cùng họ hỏng với kênh thông báo, cách đăng nhập và nguồn của orgUnit/nhân sự: BA dựng một bộ chip không có
// thật rồi người dùng bấm nhầm. Ca thật đã gặp trên màn hình (ứng dụng theo dõi khóa học bắt buộc): "khoảng
// bao nhiêu nhân viên sẽ được quản lý trong ứng dụng này, và hiện có khoảng bao nhiêu khóa học bắt buộc?" với
// bộ chip "Dưới 500 nhân viên, dưới 20 khóa học" / "500–1000 nhân viên, 20–50 khóa học" / "Trên 1000 nhân
// viên, trên 50 khóa học" — người dùng phải bỏ cả ba mà gõ vào ô "Ý khác" rằng ứng dụng dùng cho TOÀN BỘ
// nhân viên internal của nhà máy. Bậc thang đầu người hỏi đúng thứ hệ thống đang cầm, bằng thứ ngôn ngữ người
// dùng không dùng: không ai gọi chỗ mình làm là "trên 1000 nhân viên".
//
// Hai chỗ luật này sống: khối TĨNH Prompts/BusinessAnalyst/organization-scope.v1.md (thang phạm vi đã ở đó,
// và khối đi kèm mọi lời gọi BA kể cả khi OrgUnits còn trống) + nhóm «Quy mô sử dụng» của requirement-chat.v4.md.
public class BAChatUserScaleRuleTests
{
    private const string ScopePromptKey = "BusinessAnalyst/organization-scope.v1.md";
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    [Fact]
    public void ScopePrompt_TurnsTheHeadcountQuestionIntoAScopeQuestion()
    {
        var prompt = ReadPrompt(ScopePromptKey);

        Assert.Contains("HỆ QUẢ của phạm vi", prompt, StringComparison.Ordinal);
        Assert.Contains("Toàn bộ nhân viên internal của nhà máy", prompt, StringComparison.Ordinal);

        // Gọi TÊN từng bậc thang không có thật, cùng lý do như danh sách kênh Teams/SMS/Zalo: cấm chung chung
        // "đừng hỏi số lượng" thì model vẫn thấy "áng chừng bao nhiêu nhân viên" là một câu hỏi khác.
        foreach (var fake in new[] { "Dưới 500 nhân viên", "500–1000 nhân viên", "Trên 1000 nhân viên" })
        {
            Assert.Contains(fake, prompt, StringComparison.Ordinal);
        }
    }

    // Đối xứng với "email là KÊNH, không phải toàn bộ câu chuyện": biết phạm vi rồi thì vẫn còn một con số
    // KHÔNG khối ngữ cảnh nào trả lời hộ — bao nhiêu bản ghi, bao nhiêu khóa học, mỗi tháng thêm bao nhiêu.
    // Đọc rộng tay thành "đừng hỏi con số nào cả" là mất đúng con số đổi hình dạng của màn hình danh sách.
    [Fact]
    public void ScopePrompt_KeepsTheVolumeQuestionThatSurvivesTheConstant()
    {
        var prompt = ReadPrompt(ScopePromptKey);

        Assert.Contains("KHỐI LƯỢNG", prompt, StringComparison.Ordinal);
        Assert.Contains("mỗi tháng thêm bao nhiêu", prompt, StringComparison.Ordinal);
    }

    // Nhóm «Quy mô sử dụng» là chỗ luật cũ dạy đúng cái sai: nó bắt BA "dựng bậc thang theo tổng số nhân sự
    // của nhà máy" — tức là dựng đúng bộ chip đầu người mà ca thật đã bác. Chủ đề thuộc khối ranh giới phạm
    // vi nên prompt chat chỉ trỏ sang, không chép lại.
    [Fact]
    public void ChatPrompt_AsksTheScaleGroupByScopeName_NotByHeadcountLadder()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("KHÔNG dựng bậc thang đầu người", prompt, StringComparison.Ordinal);
        Assert.Contains("Toàn bộ nhân viên internal của nhà máy", prompt, StringComparison.Ordinal);
        Assert.Contains("Ranh giới phạm vi", prompt, StringComparison.Ordinal);
    }

    // Ví dụ trong prompt nặng hơn lời răn: model chép mẫu chip nó nhìn thấy. Một bộ chip đầu người còn nằm
    // trong ví dụ JSON hay trong danh sách "các câu gần như LUÔN là câu đóng" thì luật ở trên chỉ là chữ.
    [Theory]
    [InlineData("Dưới 20 người")]
    [InlineData("20–100 người")]
    [InlineData("Trên 100 người")]
    [InlineData("Áng chừng bao nhiêu người sẽ dùng ứng dụng này")]
    public void ChatPrompt_NoLongerShowsAHeadcountLadderAsAnExample(string banned)
    {
        Assert.DoesNotContain(banned, ReadPrompt(ChatPromptKey), StringComparison.OrdinalIgnoreCase);
    }

    // Nhánh dự phòng của cổng phát câu hỏi thay BA, không qua model — nó mà còn hỏi "bao nhiêu người" thì
    // luật ở prompt chỉ áp cho một nửa số lượt người dùng nhìn thấy.
    [Fact]
    public void ScaleGroupOpener_AsksWhoUsesTheApp_AndKeepsTheVolumeHalf()
    {
        var opener = CoverageGroupOpeners.Find("Quy mô sử dụng")!;

        Assert.Contains("dùng cho những ai", opener, StringComparison.Ordinal);
        Assert.Contains("toàn bộ nhân viên của nhà máy", opener, StringComparison.Ordinal);
        Assert.Contains("bao nhiêu hồ sơ", opener, StringComparison.Ordinal);
        Assert.DoesNotContain("bao nhiêu người", opener, StringComparison.OrdinalIgnoreCase);
    }

    // Prompt chỉ định hướng; điểm eval mới là thứ đo được BA có thật sự thôi dựng bộ chip đó hay không.
    [Fact]
    public void GoldenSet_ScoresTheUserScaleRule_OnChatPrompt()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == ChatPromptKey)
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c =>
            c.Contains("Trên 1000 nhân viên", StringComparison.OrdinalIgnoreCase)
            && c.Contains("Toàn bộ nhân viên internal của nhà máy", StringComparison.OrdinalIgnoreCase));
    }

    // Cùng cách tìm Prompts/ như BAChatOrgDirectoryRuleTests.
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
