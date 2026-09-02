using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Lượt chắt lọc phạm vi màn hình đã tách khỏi lượt "triển vọng phỏng vấn" — hai lời gọi, hai nhịp, hai
// file prompt. Việc tách một prompt làm đôi có đúng một cách hỏng đáng sợ: phần luật đắt nhất rơi mất
// trong lúc cắt, và không compiler nào báo. Các test dưới đây giữ hai đầu của đường cắt đó.
public class InterviewScopePromptSplitTests
{
    private const string OutlookPromptKey = "BusinessAnalyst/interview-outlook.v1.md";
    private const string ScopePromptKey = "BusinessAnalyst/interview-scope.v1.md";

    // Đặc tả trường chỉ được sống ở MỘT chỗ — cùng luật với sáu prompt bảng
    // (InterviewTablePromptTests.FieldSpec_LivesInExactlyOnePrompt). Bản sao thứ hai mọc lại ở prompt cũ
    // nghĩa là lượt chạy dày lại chở theo phần vừa được gỡ đi.
    [Fact]
    public void ScopeFieldSpec_LivesOnlyInTheScopePrompt()
    {
        Assert.Contains("`scopeAdditions`", ReadPrompt(ScopePromptKey), StringComparison.Ordinal);
        Assert.DoesNotContain("scopeAdditions", ReadPrompt(OutlookPromptKey), StringComparison.Ordinal);
    }

    // Luật đặt tên màn hình: tên ở cột `screen` đi thẳng ra "## 6. Screens To Generate" rồi thành nhãn menu
    // của bản demo, nên nó phải ngắn và bằng tiếng Anh. Đây là một trong ba nơi giữ luật đó
    // (docs/requirement-flow.md, mục "Tên màn hình là nhãn menu của bản demo") và là nơi DUY NHẤT model tự
    // đặt tên — hai nguồn kia tất định.
    [Fact]
    public void ScopePrompt_KeepsTheScreenNamingRule()
    {
        var prompt = ReadPrompt(ScopePromptKey);

        Assert.Contains("DANH TỪ CHỈ NƠI CHỐN", prompt, StringComparison.Ordinal);
        Assert.Contains("JD Library", prompt, StringComparison.Ordinal);
    }

    // Luật đắt nhất của lượt này, và là lỗi model hay mắc nhất: một CHỨC NĂNG hay một LUỒNG bị dựng thành
    // `screen`. Mỗi mục như thế thành một dòng bảng màn hình, rồi một dòng bảng phân quyền, rồi một trang
    // trống của bản demo — trong khi nó vốn là một cái nút trên màn hình đã có. Ca thật phải còn nguyên
    // trong prompt: model nhận ra mình đang viết đúng câu đó thì mới dừng được.
    [Fact]
    public void ScopePrompt_KeepsTheScreenVersusFunctionRule()
    {
        var prompt = ReadPrompt(ScopePromptKey);

        Assert.Contains("KHÔNG đưa một CHỨC NĂNG hay một LUỒNG lên làm `screen`", prompt, StringComparison.Ordinal);
        Assert.Contains("Training Plan Detail", prompt, StringComparison.Ordinal);
        Assert.Contains("người dùng MỞ nó ra hay BẤM nó?", prompt, StringComparison.Ordinal);
    }

    // Nhịp mới phải được NÓI RA trong chính prompt: quãng hội thoại model nhận nay dài hơn hẳn (cả buổi ở
    // lần gọi đầu), và không lượt nào phía sau nhặt lại phần nó bỏ sót.
    [Fact]
    public void ScopePrompt_TellsTheModelItRunsSparsely()
    {
        var prompt = ReadPrompt(ScopePromptKey);

        Assert.Contains("chạy THƯA", prompt, StringComparison.Ordinal);
        Assert.Contains("CHỜ DUYỆT", prompt, StringComparison.Ordinal);
    }

    // Đầu còn lại của đường cắt: hai danh sách ở lại phải nguyên vẹn. Chúng mới là thứ biện minh cho việc
    // lượt kia vẫn chạy sau MỖI lượt chat.
    [Fact]
    public void OutlookPrompt_StillCarriesTheTwoListsThatStayed()
    {
        var prompt = ReadPrompt(OutlookPromptKey);

        Assert.Contains("`openQuestions`", prompt, StringComparison.Ordinal);
        Assert.Contains("`workedExamples`", prompt, StringComparison.Ordinal);
        Assert.Contains("hai danh sách", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Cùng cách tìm Prompts/ như InterviewDeadEndRuleTests: ưu tiên bản copy trong bin, không có thì đi
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

        throw new FileNotFoundException("Không tìm thấy prompt " + promptKey);
    }
}
