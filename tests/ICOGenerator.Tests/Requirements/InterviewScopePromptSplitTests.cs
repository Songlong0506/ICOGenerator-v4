using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Lượt chắt lọc phạm vi (nay chắt TÁC VỤ trôi sau lần thiết kế UX đầu) đã tách khỏi lượt "triển vọng phỏng vấn", và prompt cũ của lượt ấy
// không còn tồn tại. Việc xé một prompt ra có đúng một cách hỏng đáng sợ: phần luật đắt nhất rơi mất
// trong lúc cắt, và không compiler nào báo. Các test dưới đây giữ cho đường cắt đó.
public class InterviewScopePromptSplitTests
{
    private const string RetiredOutlookPromptKey = "BusinessAnalyst/interview-outlook.v3.md";
    private const string CoveragePromptKey = "BusinessAnalyst/requirement-coverage.v5.md";
    private const string ScopePromptKey = "BusinessAnalyst/interview-scope.v1.md";
    private const string BlueprintPromptKey = "UiUx/ux-blueprint.v1.md";

    // Đặc tả trường chỉ được sống ở MỘT chỗ — cùng luật với các prompt bảng
    // (InterviewTablePromptTests.FieldSpec_LivesInExactlyOnePrompt). Bản sao thứ hai mọc lại ở prompt bản đồ
    // nghĩa là lượt chạy dày lại chở theo phần vừa được gỡ đi.
    [Fact]
    public void ScopeFieldSpec_LivesOnlyInTheScopePrompt()
    {
        Assert.Contains("`taskAdditions`", ReadPrompt(ScopePromptKey), StringComparison.Ordinal);
        Assert.DoesNotContain("taskAdditions", ReadPrompt(CoveragePromptKey), StringComparison.Ordinal);
    }

    // Luật đặt tên màn hình: tên đi thẳng ra "## 6. Screens To Generate" rồi thành nhãn menu của bản demo, nên
    // nó phải ngắn và bằng tiếng Anh. Nơi DUY NHẤT model tự đặt tên màn nay là lượt thiết kế UX.
    [Fact]
    public void BlueprintPrompt_KeepsTheScreenNamingRule()
    {
        var prompt = ReadPrompt(BlueprintPromptKey);

        Assert.Contains("DANH TỪ CHỈ NƠI CHỐN", prompt, StringComparison.Ordinal);
        Assert.Contains("JD Library", prompt, StringComparison.Ordinal);
    }

    // Lỗi đắt nhất của bảng màn hình cũ: một CHỨC NĂNG (đổi trạng thái, một form nhỏ) bị dựng thành màn hình.
    // Lượt chắt lọc nay KHÔNG nói màn hình nào cả (nó chắt tác vụ), và lượt thiết kế mang luật "đổi trạng thái
    // là một nút trên bản ghi, không bao giờ là một màn riêng" — lint còn bắt nó bằng code (transition-as-screen).
    [Fact]
    public void FunctionsNeverBecomeScreens()
    {
        Assert.Contains("KHÔNG nói màn hình, nút, menu", ReadPrompt(ScopePromptKey), StringComparison.Ordinal);
        Assert.Contains("KHÔNG BAO GIỜ là một màn riêng", ReadPrompt(BlueprintPromptKey), StringComparison.Ordinal);
    }

    // Lượt chắt lọc chạy theo LÔ sau lần thiết kế đầu và chỉ trả phần MỚI — phải nói ra trong chính prompt,
    // không thì model kể lại cả danh mục và mỗi bản kể lại là một nút thứ hai cho cùng một việc.
    [Fact]
    public void ScopePrompt_AsksOnlyForTheDelta()
    {
        var prompt = ReadPrompt(ScopePromptKey);

        Assert.Contains("DELTA", prompt, StringComparison.Ordinal);
        Assert.Contains("CHƯA XẾP CHỖ", prompt, StringComparison.Ordinal);
    }

    // Prompt cũ đã bị gỡ hẳn: còn file là còn một bản luật thứ hai chờ trôi lệch.
    [Fact]
    public void TheOldOutlookPromptIsGone()
    {
        Assert.Null(FindPrompt(RetiredOutlookPromptKey));
    }

    // Cùng cách tìm Prompts/ như InterviewDeadEndRuleTests: ưu tiên bản copy trong bin, không có thì đi
    // ngược lên repo root.
    private static string ReadPrompt(string promptKey)
        => FindPrompt(promptKey) is { } path
            ? File.ReadAllText(path)
            : throw new FileNotFoundException("Không tìm thấy prompt " + promptKey);

    // Trả null khi không có file — dùng để chốt rằng một prompt đã được gỡ HẲN, không chỉ thôi được gọi.
    private static string? FindPrompt(string promptKey)
    {
        var relative = promptKey.Replace('/', Path.DirectorySeparatorChar);

        var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts", relative);
        if (File.Exists(fromBin))
            return fromBin;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Prompts", relative);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
