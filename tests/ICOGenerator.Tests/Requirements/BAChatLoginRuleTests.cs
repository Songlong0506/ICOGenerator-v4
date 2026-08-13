using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Mọi ứng dụng ở nhà máy đăng nhập bằng SSO qua IdentityServer, dùng tài khoản Bosch sẵn có.
//
// Hằng số của MÔI TRƯỜNG, cùng hạng với ranh giới phạm vi và kênh thông báo email. Khác hai khối kia ở
// một điểm khiến nó nguy hiểm hơn: prompt chat TỪNG tự tay mời BA hỏi về đăng nhập. Mục "Đối tượng người
// dùng" nêu *"Người dùng cần đăng nhập riêng cho mỗi người không?"* làm VÍ DỤ MẪU của một câu hỏi đúng
// tầm nghiệp vụ — nghe thì đúng là nghiệp vụ, nhưng thứ nó hỏi đã chốt từ trước, và câu trả lời "cả tổ
// dùng chung một tài khoản" thì không hiện thực được mà vẫn chảy qua "Điều đã chốt" → Product Brief →
// thiết kế. Test này giữ cho ví dụ đó không quay lại, và giữ cho ràng buộc sống đúng chỗ của nó.
//
// Khối ràng buộc ở Prompts/BusinessAnalyst/organization-platform.v1.md, được OrganizationContextService
// đính vào MỌI lời gọi BA (xem OrganizationContextServiceTests).
public class BAChatLoginRuleTests
{
    private const string PlatformPromptKey = "BusinessAnalyst/organization-platform.v1.md";
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    [Fact]
    public void PlatformPrompt_StatesSsoIsTheOnlyLogin_AndBansTheOnesThatDoNotExist()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.Contains("SSO qua IdentityServer", prompt, StringComparison.OrdinalIgnoreCase);

        // Gọi TÊN từng phương án không có thật, cùng lý do như danh sách kênh Teams/SMS/Zalo: cấm chung
        // chung "cách đăng nhập khác" thì model vẫn thấy "tài khoản nội bộ cho nhà thầu" là ngoại lệ hợp lý.
        foreach (var fake in new[] { "username/password", "Đăng ký tài khoản mới", "Google", "dùng chung" })
        {
            Assert.Contains(fake, prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Đối xứng với "email là KÊNH, không phải toàn bộ câu chuyện": chốt cách đăng nhập không được đọc thành
    // "khỏi hỏi gì quanh chuyện ai vào được app nữa". Vế bị mất đắt nhất là nhân viên external — chính chỗ
    // SSO ngừng phủ hết người dùng, và organization-scope.v1.md đã khẳng định đó là câu hỏi hợp lệ.
    [Fact]
    public void PlatformPrompt_KeepsTheBusinessQuestionsThatSurviveTheConstant()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.Contains("AI được vào ứng dụng", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("external", prompt, StringComparison.OrdinalIgnoreCase);

        // Vai trò được gán từ đâu là câu hỏi về NGUỒN, khác câu "mỗi vai được làm gì" (chốt bằng bảng).
        Assert.Contains("vai nào", prompt, StringComparison.OrdinalIgnoreCase);

        // Đăng nhập dựa trên tài khoản Bosch ⇒ danh tính nhân viên đã có nguồn, đừng hỏi lấy từ đâu.
        Assert.Contains("danh sách nhân viên lấy từ đâu", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Cùng bài học đã phải học ở khối ranh giới phạm vi và khối kênh thông báo.
    [Fact]
    public void PlatformPrompt_IsAProductConstant_NotSomethingTheUserSaid()
    {
        var prompt = ReadPrompt(PlatformPromptKey);

        Assert.Contains("đăng nhập bằng SSO", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bóp méo", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Ca thật đã nằm sẵn trong prompt chat suốt một thời gian dài: câu hỏi này được nêu như ví dụ ĐÚNG.
    // Nó phải biến khỏi vai trò đó, và phải xuất hiện lại như một điều BỊ CẤM.
    [Fact]
    public void ChatPrompt_NoLongerOffersTheLoginQuestionAsAGoodExample()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.DoesNotContain("Đăng nhập bằng SSO hay tài khoản nội bộ", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Đăng nhập KHÔNG phải một câu hỏi ở đây", prompt, StringComparison.OrdinalIgnoreCase);

        // Chủ đề thuộc file khác ⇒ prompt chat chỉ trỏ sang, không chép lại nội dung ràng buộc.
        Assert.Contains("Nền tảng đã chốt của nhà máy", prompt, StringComparison.Ordinal);
    }

    // Prompt chỉ định hướng; điểm eval mới là thứ đo được BA có thật sự thôi hỏi hay không.
    [Fact]
    public void GoldenSet_ScoresTheLoginRule_OnChatPrompt()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == ChatPromptKey)
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c =>
            c.Contains("tài khoản riêng", StringComparison.OrdinalIgnoreCase)
            && c.Contains("SSO", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(criteria, c => c.Contains("external", StringComparison.OrdinalIgnoreCase));
    }

    // Cùng cách tìm Prompts/ như BAChatNotificationChannelRuleTests.
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
