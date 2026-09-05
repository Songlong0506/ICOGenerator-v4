using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Lượt chắt lọc phạm vi màn hình đã tách khỏi lượt "triển vọng phỏng vấn" — và phần còn lại của lượt ấy
// (ví dụ đã xác nhận) sau đó nhập vào lượt chắt lọc bản đồ bao phủ, nên prompt cũ không còn tồn tại. Việc
// xé một prompt ra làm hai chỗ có đúng một cách hỏng đáng sợ: phần luật đắt nhất rơi mất trong lúc cắt, và
// không compiler nào báo. Các test dưới đây giữ hai đầu của đường cắt đó.
public class InterviewScopePromptSplitTests
{
    private const string RetiredOutlookPromptKey = "BusinessAnalyst/interview-outlook.v3.md";
    private const string CoveragePromptKey = "BusinessAnalyst/requirement-coverage.v5.md";
    private const string ScopePromptKey = "BusinessAnalyst/interview-scope.v1.md";

    // Đặc tả trường chỉ được sống ở MỘT chỗ — cùng luật với sáu prompt bảng
    // (InterviewTablePromptTests.FieldSpec_LivesInExactlyOnePrompt). Bản sao thứ hai mọc lại ở prompt cũ
    // nghĩa là lượt chạy dày lại chở theo phần vừa được gỡ đi.
    [Fact]
    public void ScopeFieldSpec_LivesOnlyInTheScopePrompt()
    {
        Assert.Contains("`scopeAdditions`", ReadPrompt(ScopePromptKey), StringComparison.Ordinal);
        Assert.DoesNotContain("scopeAdditions", ReadPrompt(CoveragePromptKey), StringComparison.Ordinal);
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

    // Đầu còn lại của đường cắt: phần ở lại (ví dụ đã xác nhận) phải sống trong prompt bao phủ, và luật
    // đắt nhất của nó — cặp đầu vào → kết quả, ví dụ bị bác thì xoá — phải đi cùng, không rơi lại trong
    // file cũ. Prompt cũ đã bị gỡ hẳn: còn file là còn một bản luật thứ hai chờ trôi lệch.
    [Fact]
    public void CoveragePrompt_CarriesTheWorkedExamples_AndTheOldPromptIsGone()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("`workedExamples`", prompt, StringComparison.Ordinal);
        Assert.Contains("ĐẦU VÀO CỤ THỂ", prompt, StringComparison.Ordinal);
        Assert.Contains("BÁC BỎ thì XOÁ", prompt, StringComparison.Ordinal);

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
