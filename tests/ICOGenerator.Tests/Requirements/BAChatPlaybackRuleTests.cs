using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// QUY TẮC PHÁT LẠI: hỏi bổ sung thì phải chép lại điều đã ghi nhận, cấm tham chiếu suông.
//
// Ca thật đã gặp trên màn hình (app lập kế hoạch lớp học cho nhân viên): người dùng kể rất kỹ thông tin
// của LỚP HỌC ở một lượt trước, mấy lượt sau BA hỏi
//
//   "Ngoài thông tin của lớp học đã nêu, mỗi khóa học bắt buộc hoặc tùy chọn cần quản lý thêm những
//    thông tin nào? Anh/chị có thể mô tả các trường thông tin và mối liên hệ giữa khóa học, nhân viên,
//    nhu cầu học và lớp học."
//
// Cụm "đã nêu" trỏ tới một chỗ mà CHỈ MÌNH BA đang nhìn thấy: BA có cả cuộn hội thoại trong ngữ cảnh,
// người dùng chỉ thấy ô chat cuối cùng. Muốn trả lời họ phải cuộn ngược lên đọc lại chính lời mình —
// phần lớn sẽ không cuộn mà trả lời đại một câu chung chung, rồi câu đó được chắt vào bản đồ bao phủ và
// bản đồ bao phủ NHƯ CÂU TRẢ LỜI THẬT, nhóm coi như đã hỏi xong và BA không quay lại nữa. Cùng hạng
// thiệt hại với "câu mở mà vẫn kèm chip", chỉ đi bằng cửa khác.
//
// Vì sao chốt bằng test này chứ không bằng một phanh trong BAChatReplyParser: các phanh ở đó đều sửa
// được bằng máy mà không cần biết nội dung (bỏ chip của câu mở, hạ multiSelect, cắt câu quá trần). Ở đây
// thì không — thứ còn thiếu là DANH SÁCH người dùng đã kể, và máy không thể tự dựng lại nó mà không bịa.
// Nên tầng chặn thật là prompt + golden set eval, và test này giữ cho hai thứ đó không âm thầm rơi mất:
// prompt phải còn mang quy tắc, golden set phải còn scenario chấm điểm nó.
public class BAChatPlaybackRuleTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    [Fact]
    public void ChatPrompt_CarriesThePlaybackRule_AndBansDanglingReferences()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("QUY TẮC PHÁT LẠI", prompt, StringComparison.Ordinal);

        // Các cụm tham chiếu suông phải được gọi tên ra để BA nhận diện được chính mình đang viết chúng.
        foreach (var phrase in new[] { "như đã nêu", "ngoài những thông tin trên", "như đã đề cập" })
            Assert.Contains(phrase, prompt, StringComparison.OrdinalIgnoreCase);

        // Vế thứ hai của cùng câu hỏi hỏng: bắt người dùng nghiệp vụ vẽ hộ mô hình dữ liệu.
        Assert.Contains("mối liên hệ giữa các đối tượng", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Prompt chỉ định hướng; điểm eval mới là thứ đo được BA có thật sự phát lại hay không. Xoá scenario
    // đi là quy tắc trên mất chỗ chấm điểm mà không ai biết.
    [Fact]
    public void GoldenSet_ScoresThePlaybackRule_OnChatPrompt()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == ChatPromptKey)
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c =>
            c.Contains("CHÉP LẠI", StringComparison.Ordinal)
            && c.Contains("tham chiếu suông", StringComparison.OrdinalIgnoreCase));

        // Vế mô hình dữ liệu đi kèm: hỏi trường + quan hệ trong một câu là lỗi hay xuất hiện cùng lúc.
        Assert.Contains(criteria, c => c.Contains("mô hình dữ liệu", StringComparison.OrdinalIgnoreCase));
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
