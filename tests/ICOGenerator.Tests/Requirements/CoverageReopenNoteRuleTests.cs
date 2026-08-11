using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cụm "người dùng báo phần này chưa đúng" là một GIAO ƯỚC giữa prompt và code, và nó không có mối nối nào
// mà compiler kiểm được: lượt chắt lọc bản đồ (LLM) ghi cụm này vào dòng bị đính chính, còn
// AskedQuestionHistory.ReopenedGroups đọc nó bằng Contains để miễn phanh chống-hỏi-lại cho đúng nhóm ấy.
//
// Từ khi panel "Tiến độ khai thác" bỏ nút "chưa đúng?" (nhãn nhóm là từ vựng nội bộ của BA — người dùng
// nghiệp vụ không biết mình đang phản đối cái gì), đây là đường DUY NHẤT để thoát khỏi một dòng bị chấm
// [RÕ] oan: BA bị cấm hỏi lại nhóm đã [RÕ], nên nhóm đó sẽ không bao giờ được nhắc tới nữa. Một lần dọn
// prompt viết lại cụm đó cho "mượt hơn" là đủ làm đứt mối nối, mà triệu chứng thì hoàn toàn im lặng: bản
// đồ vẫn hạ nhóm xuống, chỉ là câu hỏi của BA bị lọc mất vì trùng câu cũ, và người dùng thấy lời đính
// chính của mình rơi vào hư không.
public class CoverageReopenNoteRuleTests
{
    private const string CoveragePromptKey = "BusinessAnalyst/requirement-coverage.v3.md";

    [Fact]
    public void CoveragePrompt_WritesTheExactMarkerThatExemptsTheAntiRepeatBrake()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        Assert.Contains(AskedQuestionHistory.ReopenNote, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CoveragePrompt_TellsTheDistillerWhenToWriteIt()
    {
        var prompt = ReadPrompt(CoveragePromptKey);

        // Điều kiện kích hoạt: người dùng phủ nhận/sửa lại điều bản đồ đang ghi nhận — và nhận diện theo
        // NỘI DUNG họ đính chính, vì họ không nhìn thấy (và không biết) tên 12 nhóm.
        Assert.Contains("Người dùng đính chính một nhóm", prompt, StringComparison.Ordinal);
        Assert.Contains("đúng nguyên văn", prompt, StringComparison.Ordinal);

        // Hạ xuống [MỘT PHẦN] chứ không xoá trắng: ghi nhận cũ phải còn lại để BA biết mình hiểu gì và bị
        // phủ nhận điều gì, thay vì hỏi lại nhóm đó từ số không.
        Assert.Contains("ghi nhận trước đó", prompt, StringComparison.Ordinal);
    }

    // Cùng cách tìm Prompts/ như BAChatScopeConflictRuleTests: ưu tiên bản copy trong bin, không có thì đi
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
