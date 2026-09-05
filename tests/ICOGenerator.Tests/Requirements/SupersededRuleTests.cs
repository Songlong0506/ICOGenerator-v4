using Xunit;

namespace ICOGenerator.Tests.Requirements;

// MỘT QUY TẮC BỊ NGƯỜI DÙNG BÁC BỎ VẪN SỐNG TIẾP TRONG CÁC TẦNG TRÍ NHỚ.
//
// Các tầng chắt lọc của buổi phỏng vấn — bộ nhớ hội thoại, bản đồ bao phủ, danh sách ví dụ vàng — đều là
// GỘP LŨY TIẾN: một dòng đã viết ra sẽ ở lại mãi mãi trừ khi chính lượt chắt lọc gỡ nó. Mà người
// dùng đổi ý là chuyện thường xuyên, và họ đổi ý bằng cách nói một câu mới chứ không bằng cách chỉ vào
// dòng cũ.
//
// Ca thật (dự án Learning and Development 7):
//
//   Lượt 14  BA:         "23 nhân viên, sĩ số tối thiểu 8 và tối đa 12 thì hệ thống gợi ý mở 2 lớp,
//                         phân bổ 12 và 11 người — đúng cách tính không?"
//   Lượt 15  Người dùng: "Đúng cách tính này"                                   (một cú bấm, 4 token)
//   Lượt 35  Người dùng: "assistant chỉ cần quan tâm việc mở bao nhiêu lớp… còn 1 lớp có bao nhiêu học
//                         viên thì không cần quan tâm, khi 1 lớp được mở ra thì nhân viên tự đăng ký"
//
// Vế "phân bổ 12 và 11" vừa bị bác bỏ, nhưng bộ nhớ hội thoại vẫn chở nguyên nó, cạnh dòng mới nói ngược
// lại — và các tầng sau (Product Brief → AI Design Spec → oracle chấm POC) không có cách nào biết dòng
// nào mới hơn. Gốc của việc đó nằm ở lượt 14: một ví dụ tính thử chở HAI quy tắc, nên một chữ ký ký cho
// cả hai.
public class SupersededRuleTests
{
    // Chặn ở GỐC: một ví dụ chỉ được chốt một quy tắc.
    [Fact]
    public void ChatPrompt_RequiresOneRulePerWorkedExample()
    {
        var prompt = ReadPrompt("BusinessAnalyst/requirement-chat.v4.md");

        Assert.Contains("MỖI ví dụ chốt ĐÚNG MỘT quy tắc", prompt, StringComparison.Ordinal);

        // Phép thử phải viết ra được, không chỉ là một lời khuyên chung chung — "đừng gộp" thì model nào
        // cũng đồng ý mà vẫn gộp.
        Assert.Contains("bỏ đi một nửa ví dụ", prompt, StringComparison.OrdinalIgnoreCase);
        // Và ca thật phải còn tên các con số của nó: một luật không có ví dụ hỏng đi kèm thì lần đọc thứ
        // hai model chỉ thấy một câu đạo đức.
        Assert.Contains("12 và 11", prompt, StringComparison.Ordinal);
    }

    // Chặn ở ĐƯỜNG RA: tầng nhớ dài hạn phải gỡ vế đã bị bác, không để nó nằm cạnh bản mới.
    [Fact]
    public void ConversationSummary_MustDropWhatWasSuperseded()
    {
        var prompt = ReadPrompt("BusinessAnalyst/conversation-summary.v1.md");

        Assert.Contains("THAY THẾ", prompt, StringComparison.Ordinal);
        Assert.Contains("gộp lũy tiến", prompt, StringComparison.OrdinalIgnoreCase);
        // Ranh giới ngược lại phải có mặt, nếu không luật này thành cái cớ để xóa mất thông tin: bổ sung
        // hay nói rõ hơn KHÔNG phải thay thế.
        Assert.Contains("bổ sung", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Ví dụ vàng là oracle chấm POC, nên một ví dụ đã bị bác mà còn nằm lại thì bản demo bị chấm theo đúng
    // cái sai đó — không cổng nào phía sau bắt được.
    [Fact]
    public void CoverageDistill_MustDropASupersededWorkedExample()
    {
        var prompt = ReadPrompt("BusinessAnalyst/requirement-coverage.v5.md");

        Assert.Contains("BÁC BỎ thì XOÁ", prompt, StringComparison.Ordinal);
        Assert.Contains("23 người, sĩ số 8–12", prompt, StringComparison.Ordinal);
    }

    // Cùng cách tìm Prompts/ như BAChatNotificationChannelRuleTests: ưu tiên bản copy trong bin, không có
    // thì đi ngược lên repo root.
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
