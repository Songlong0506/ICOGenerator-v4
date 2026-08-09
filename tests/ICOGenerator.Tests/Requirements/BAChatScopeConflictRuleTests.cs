using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Khối ngữ cảnh hệ thống KHÔNG phải lời người dùng.
//
// Ca thật đã gặp trên màn hình (app lập kế hoạch lớp học cho nhân viên), hỏng theo hai nhịp nối nhau:
//
//   Người dùng: "đây là app để ... đánh giá kết quả học tập của TẤT CẢ NHÂN VIÊN BOSCH trong 1 năm"
//   BA:         "Mình GHI NHẬN ứng dụng dùng để ... của toàn bộ nhân viên Bosch ĐỒNG NAI ..."
//   … người dùng kể một lượt rất dài về vai trò + toàn bộ luồng nghiệp vụ …
//   BA:         "Mình cần xác nhận lại phạm vi áp dụng: anh/chị vừa mô tả **tất cả nhân viên Bosch**,
//                trong khi phạm vi ứng dụng đang được ghi nhận là **toàn bộ nhân viên Bosch tại nhà máy
//                Đồng Nai**. Phạm vi nào đúng với ứng dụng này ạ?"
//
// Chữ "Đồng Nai" đến từ khối ranh giới phạm vi (Prompts/BusinessAnalyst/organization-scope.v1.md) mà
// OrganizationContextService đính vào MỌI lời gọi BA — nó là hằng số của SẢN PHẨM, người dùng chưa hề nói
// và cũng không nhìn thấy khối đó. Ba tầng thiệt hại:
//
//  1. Câu "mình ghi nhận" chèn dữ kiện lạ vào lời người dùng ⇒ DecisionLogService chắt nó thành một dòng
//     "Điều đã chốt" NHƯ MỘT QUYẾT ĐỊNH CỦA HỌ, rồi dòng đó nằm trong ngữ cảnh của mọi lượt sau.
//  2. Lượt sau, BA đem chính câu mình viết ra chọi với lời người dùng và bắt họ phân xử — một mâu thuẫn
//     không có thật (người ngồi trong nhà máy Đồng Nai nói "tất cả nhân viên Bosch" là cách nói rộng
//     miệng, hai vế cùng đúng), lại đúng vào điều organization-scope.v1.md cấm hỏi vì ĐÃ CHỐT.
//  3. Câu hỏi đó mở đường cho người dùng chọn nghĩa rộng "tất cả nhân viên Bosch" — chính phương án vượt
//     nhà máy mà cùng file đó cấm BA đưa ra; và vì lượt gỡ mâu thuẫn phải đứng MỘT MÌNH, lượt kể dài về
//     vai trò + luồng nghiệp vụ ngay trước đó bị bỏ trắng không ghi nhận.
//
// Không có phanh máy nào bắt được lỗi này trong BAChatReplyParser: muốn biết "Đồng Nai" là bịa hay không
// thì phải so với những gì người dùng đã nói, mà so được thì cũng không sửa được câu hỏi hộ BA. Nên tầng
// chặn thật là prompt + golden set eval, và test này giữ cho hai thứ đó không âm thầm rơi mất.
public class BAChatScopeConflictRuleTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";
    private const string ScopePromptKey = "BusinessAnalyst/organization-scope.v1.md";

    [Fact]
    public void ChatPrompt_RequiresBothSidesOfAConflictToBeTheUsersOwnWords()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("Hai vế phải cùng là lời NGƯỜI DÙNG", prompt, StringComparison.Ordinal);

        // Ba nguồn không bao giờ được làm một vế của mâu thuẫn.
        Assert.Contains("ranh giới phạm vi", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mình ghi nhận", prompt, StringComparison.OrdinalIgnoreCase);

        // Ca thật phải còn nguyên trong prompt: BA nhận ra mình đang viết đúng câu đó thì mới dừng được.
        Assert.Contains("đang được ghi nhận là", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChatPrompt_BansSmugglingSystemContextIntoTheAcknowledgement()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("chỉ được chứa điều người dùng THẬT SỰ đã nói", prompt, StringComparison.Ordinal);

        // Lý do phải nằm cạnh quy tắc: câu ghi nhận sai lệch bị chắt vào "Điều đã chốt" như lời người dùng.
        Assert.Contains("Điều đã chốt", prompt, StringComparison.Ordinal);
    }

    // Khối ranh giới phạm vi được đính vào MỌI lời gọi BA, kể cả khi Prompt Studio override khối ngữ cảnh
    // render ⇒ quy tắc "nói "tất cả nhân viên Bosch" là hiểu ngầm toàn nhà máy" phải sống ở đây, không chỉ
    // ở prompt chat.
    [Fact]
    public void ScopePrompt_ReadsCompanyWideWordingAsThePlantItself()
    {
        var prompt = ReadPrompt(ScopePromptKey);

        Assert.Contains("tất cả nhân viên Bosch", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("toàn công ty", prompt, StringComparison.OrdinalIgnoreCase);

        // Hằng số của sản phẩm: dùng để khỏi hỏi thừa, không dùng để kể lại lời người dùng.
        Assert.Contains("hằng số của SẢN PHẨM", prompt, StringComparison.Ordinal);
    }

    // Prompt chỉ định hướng; điểm eval mới là thứ đo được BA có thật sự thôi chất vấn phạm vi hay không.
    [Fact]
    public void GoldenSet_ScoresTheScopeRules_OnChatPrompt()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == ChatPromptKey)
            .Select(s => s.Criteria)
            .ToList();

        // Không được biến ranh giới phạm vi thành câu hỏi xác nhận.
        Assert.Contains(criteria, c => c.Contains("hỏi xác nhận phạm vi", StringComparison.OrdinalIgnoreCase));

        // Không được gán "Đồng Nai" cho người dùng trong câu ghi nhận.
        Assert.Contains(criteria, c =>
            c.Contains("mình ghi nhận", StringComparison.OrdinalIgnoreCase)
            && c.Contains("Đồng Nai", StringComparison.OrdinalIgnoreCase));
    }

    // Cùng cách tìm Prompts/ như EvalScenariosSeedDataTests: ưu tiên bản copy trong bin, không có thì đi
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
