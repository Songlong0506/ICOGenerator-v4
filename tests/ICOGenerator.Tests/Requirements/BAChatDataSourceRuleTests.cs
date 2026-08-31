using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Dữ liệu của ứng dụng đến từ đâu là câu hỏi NGHIỆP VỤ; hai hệ thống nối nhau bằng gì là câu hỏi KỸ THUẬT.
//
// Prompt chat cấm hỏi chuyện kỹ thuật, và trước đây danh sách cấm gộp luôn "tích hợp hệ thống ngoài" —
// tức là cấm cả vế nghiệp vụ. Hậu quả không lộ ra trong hội thoại mà lộ ở cuối đường: tài liệu im lặng
// về nguồn ⇒ bước soạn tài liệu mặc định là nhập tay ⇒ POC seed một màn hình CRUD đầy nút Thêm/Sửa/Xóa
// cho danh sách nhân viên mà thực tế HR đổ sang hằng tháng. Cùng loại thiệt hại với cột `Revision Number`
// của hệ cũ nằm lại trong màn hình app mới, chỉ khác là sai cả một màn hình chứ không phải một trường.
//
// Ràng buộc có ĐIỀU KIỆN KÍCH HOẠT (chỉ hỏi khi người dùng tự nhắc tới một hệ thống/file đang dùng), nên
// test giữ cả hai chiều: chiều hỏi, và chiều KHÔNG được biến nó thành câu hỏi rải cho mọi danh mục.
public class BAChatDataSourceRuleTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";
    private const string CoveragePromptKey = "BusinessAnalyst/requirement-coverage.v4.md";

    [Fact]
    public void ChatPrompt_AsksWhereDataComesFrom_ButNotHowSystemsConnect()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("NGUỒN của dữ liệu", prompt, StringComparison.Ordinal);

        // Hai vế phải chốt — cả hai đều trả lời được bằng ngôn ngữ nghiệp vụ.
        Assert.Contains("Có người tải file lên", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cập nhật khi nào", prompt, StringComparison.OrdinalIgnoreCase);

        // Vế kỹ thuật phải bị gọi TÊN, vì "không hỏi kỹ thuật" chung chung thì model vẫn thấy hỏi
        // "real-time hay chạy lô?" là một câu hỏi nghiệp vụ hợp lý.
        foreach (var technical in new[] { "webhook", "real-time", "chạy lô" })
        {
            Assert.Contains(technical, prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Điều kiện kích hoạt là thứ giữ cho quy tắc này không tự biến thành một vòng hỏi vô nghĩa.
    [Fact]
    public void ChatPrompt_OnlyAsksWhenTheUserBroughtUpASource()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("Điều kiện kích hoạt", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Không đi hỏi nguồn cho mọi danh mục", prompt, StringComparison.OrdinalIgnoreCase);

        // Cùng câu nói cũng kích hoạt luật xin file — và xin file phải đứng MỘT MÌNH, nên hai việc này
        // không được dồn vào một lượt.
        Assert.Contains("lượt đó chỉ xin file", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Chốt chặn quan trọng nhất của phía coverage: KHÔNG được giữ dòng ở [MỘT PHẦN] vì "chưa biết nguồn"
    // khi người dùng chưa hề nhắc tới nguồn nào. Đó đúng là hình dạng của vòng lặp câu hỏi chết mà
    // CoverageDeadQuestionLoopTests đã phải dựng lưới một lần: dòng kẹt ⇒ cổng readiness thay lời mời
    // "Write Requirement" bằng một câu hỏi dựng sẵn ⇒ người dùng không biết trả lời gì ⇒ bản đồ không đổi
    // ⇒ lượt sau lặp lại y nguyên.
    [Fact]
    public void CoveragePrompt_DoesNotHoldTheRowPartial_WhenNoSourceWasEverMentioned()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains("mặc định dữ liệu do chính ứng dụng quản lý", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vòng lặp câu hỏi chết", prompt, StringComparison.OrdinalIgnoreCase);

        // Và không được đẻ ra một mẩu `gap` mà BA bị cấm đi hỏi.
        Assert.Contains("`gap`: *cách tích hợp", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GoldenSet_ScoresTheDataSourceRule_OnChatPrompt()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == ChatPromptKey)
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c =>
            c.Contains("vào ứng dụng bằng đường nào", StringComparison.OrdinalIgnoreCase)
            && c.Contains("webhook", StringComparison.OrdinalIgnoreCase));
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
