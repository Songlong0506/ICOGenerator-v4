using Xunit;

namespace ICOGenerator.Tests.Requirements;

// TÊN của mô hình dữ liệu — tên đối tượng, tên thông tin, tên trạng thái — viết bằng TIẾNG ANH; phần diễn
// giải giữ tiếng Việt. Cùng luật đã áp cho tên màn hình và tên báo cáo, và vì cùng một lý do: cái TÊN là thứ
// chảy ra giao diện và ra mô hình dữ liệu, còn phần diễn giải đã có ô riêng ngay bên cạnh.
//
// Buổi phỏng vấn không còn bảng đối tượng nào: tên trường và tên trạng thái do bước sinh AI Design Spec tự
// đặt (ở màn hình và quy tắc của spec). Luật vì vậy sống ở hai tầng đọc cái tên (prompt sinh spec → hai prompt POC), và sửa một chỗ mà
// bỏ chỗ kia thì hỏng theo kiểu không ai thấy — model vẫn trả đủ, chỉ là mỗi tầng một kiểu tên.
//
// Đầy đủ ở docs/requirement-flow.md, mục "Không có bảng đối tượng nghiệp vụ".
public class BAChatEntityNamingRuleTests
{
    private const string SpecPromptKey = "BusinessAnalyst/ai-design-spec.v1.md";
    private const string PocPromptKey = "Developer/poc-preview.v1.md";
    private const string VisualReviewPromptKey = "UiUx/poc-visual-review.v1.md";

    // Bước sinh spec tự ĐẶT tên trường/trạng thái — và đặt bằng tên hiển thị tiếng Anh.
    [Fact]
    public void SpecPrompt_NamesFieldsAndStatesInEnglishDisplayNames()
    {
        var prompt = ReadPrompt(SpecPromptKey);

        Assert.Contains("## 6. Screens To Generate", prompt, StringComparison.Ordinal);
        Assert.Contains("Title Case", prompt, StringComparison.Ordinal);
        Assert.Contains("Effective Date", prompt, StringComparison.Ordinal);
    }

    // Luật ngôn ngữ của POC: nhãn giao diện LUÔN tiếng Anh, chỉ nội dung nghiệp vụ được tiếng Việt. Prompt
    // sinh POC và tầng soi ảnh phải nói CÙNG một định nghĩa — lệch nhau thì Visual QA nhặt về finding "sai
    // ngôn ngữ" cho đúng thứ prompt sinh vừa dặn làm, hoặc bỏ qua một nút "Tạo JD" lọt giữa khung tiếng Anh.
    [Fact]
    public void PocPrompts_RequireEnglishChromeAndAllowVietnameseContent()
    {
        var poc = ReadPrompt(PocPromptKey);
        Assert.Contains("NHÃN GIAO DIỆN LUÔN TIẾNG ANH", poc, StringComparison.Ordinal);
        Assert.Contains("NỘI DUNG nghiệp vụ", poc, StringComparison.Ordinal);
        Assert.Contains("CHÉP NGUYÊN VĂN, KHÔNG dịch", poc, StringComparison.Ordinal);

        var review = ReadPrompt(VisualReviewPromptKey);
        Assert.Contains("NHÃN GIAO DIỆN", review, StringComparison.Ordinal);
        Assert.Contains("KHÔNG tính là lỗi", review, StringComparison.Ordinal);
        Assert.Contains("NỘI DUNG nghiệp vụ bằng tiếng Việt", review, StringComparison.Ordinal);
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
