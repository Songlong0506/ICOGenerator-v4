using Xunit;

namespace ICOGenerator.Tests.Requirements;

// TÊN của mô hình dữ liệu — tên đối tượng, tên thông tin, tên trạng thái — viết bằng TIẾNG ANH; phần diễn
// giải giữ tiếng Việt. Cùng luật đã áp cho tên màn hình và tên báo cáo, và vì cùng một lý do: cái TÊN là thứ
// chảy ra giao diện và ra mô hình dữ liệu, còn phần diễn giải đã có ô riêng ngay bên cạnh.
//
// Buổi phỏng vấn không còn bảng đối tượng nào: tên trường và tên trạng thái do bước sinh AI Design Spec tự
// đặt (ở màn hình và quy tắc của spec), và tên trạng thái đi vào đó từ bảng thông báo (mỗi dòng là một
// trạng thái). Luật vì vậy sống ở BA chỗ theo
// đúng ba tầng đọc cái tên (khối lệnh bày bảng thông báo → prompt sinh spec → hai prompt POC), và sửa một chỗ
// mà bỏ hai chỗ kia thì hỏng theo kiểu không ai thấy — model vẫn trả đủ, chỉ là mỗi tầng một kiểu tên.
//
// Đầy đủ ở docs/requirement-flow.md, mục "Không có bảng đối tượng nghiệp vụ".
public class BAChatEntityNamingRuleTests
{
    private const string NotificationTablePromptKey = "BusinessAnalyst/table-notification-map.v1.md";
    private const string SpecPromptKey = "BusinessAnalyst/ai-design-spec.v1.md";
    private const string PocPromptKey = "Developer/poc-preview.v1.md";
    private const string VisualReviewPromptKey = "UiUx/poc-visual-review.v1.md";

    // Tên bản ghi và tên trạng thái của bảng thông báo là nguồn tên trạng thái của mô hình dữ liệu, nên chúng
    // phải ra đúng dạng HIỂN THỊ tiếng Anh ngay từ đây — còn ô "khi nào" vẫn là lời người dùng, tiếng Việt.
    [Fact]
    public void NotificationTablePrompt_NamesEntityAndEventInEnglish_AndKeepsTheTriggerVietnamese()
    {
        var prompt = ReadPrompt(NotificationTablePromptKey);

        Assert.Contains("`entity`", prompt, StringComparison.Ordinal);
        Assert.Contains("`event`", prompt, StringComparison.Ordinal);
        Assert.Contains("Title Case", prompt, StringComparison.Ordinal);
        Assert.Contains("`trigger`", prompt, StringComparison.Ordinal);
        Assert.Contains("**bằng tiếng Việt**", prompt, StringComparison.Ordinal);
    }

    // Bước sinh spec tự ĐẶT tên trường/trạng thái, nhưng tên trạng thái người dùng đã thấy ở bảng thông báo
    // thì nó chỉ CHÉP — dịch lại ở đây là ba tầng sau đọc một bộ tên khác với bộ người dùng vừa rà.
    [Fact]
    public void SpecPrompt_NamesFieldsAndStatesInEnglishDisplayNames_AndCopiesTheNotificationStates()
    {
        var prompt = ReadPrompt(SpecPromptKey);

        Assert.Contains("## 6. Screens To Generate", prompt, StringComparison.Ordinal);
        Assert.Contains("Title Case", prompt, StringComparison.Ordinal);
        Assert.Contains("Effective Date", prompt, StringComparison.Ordinal);
        Assert.Contains("Bảng thông báo / nhắc nhở người dùng ĐÃ CHỐT", prompt, StringComparison.Ordinal);
        Assert.Contains("chép ĐÚNG CHỮ", prompt, StringComparison.Ordinal);
    }

    // Luật ngôn ngữ của POC ("spec tiếng Việt → UI tiếng Việt") và tầng soi ảnh của nó phải BIẾT ngoại lệ
    // này. Không có ngoại lệ thì mỗi lần review POC lại nhặt về một mớ finding "sai ngôn ngữ" cho đúng bộ
    // nhãn mà spec cố ý viết bằng tiếng Anh — và cách "sửa" duy nhất là dịch chúng đi, tức lật lại quyết
    // định của người dùng ở tầng cuối cùng.
    [Fact]
    public void PocPrompts_TreatEnglishNameLabelsInAVietnameseUiAsCorrect()
    {
        var poc = ReadPrompt(PocPromptKey);
        Assert.Contains("NGÔN NGỮ:", poc, StringComparison.Ordinal);
        Assert.Contains("CHÉP NGUYÊN VĂN, KHÔNG dịch", poc, StringComparison.Ordinal);

        var review = ReadPrompt(VisualReviewPromptKey);
        Assert.Contains("KHÔNG tính là lỗi", review, StringComparison.Ordinal);
        Assert.Contains("chip trạng thái", review, StringComparison.Ordinal);
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
