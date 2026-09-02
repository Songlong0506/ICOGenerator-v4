using Xunit;

namespace ICOGenerator.Tests.Requirements;

// BA CỘT TÊN của bảng đối tượng — tên đối tượng, tên thông tin, tên trạng thái — viết bằng TIẾNG ANH; mọi
// ô còn lại giữ tiếng Việt. Cùng luật đã áp cho tên màn hình và tên báo cáo, và vì cùng một lý do: cái TÊN
// là thứ chảy ra giao diện và ra mô hình dữ liệu, còn phần diễn giải đã có ô riêng ngay bên cạnh.
//
// Vì sao cần một test giữ luật này chứ không chỉ ghi vào prompt: nó sống ở BỐN chỗ theo đúng bốn tầng đọc
// cái tên (prompt chat → khối lệnh theo lượt → prompt sinh spec → hai prompt POC), và sửa một chỗ mà bỏ ba
// chỗ kia thì hỏng theo kiểu không ai thấy — model vẫn trả về một bảng đầy đủ, chỉ là mỗi lượt một kiểu
// tên. Ca cụ thể mà luật này vá: EntityMapBuilder.ManagedListScreens ghép "<tên thông tin> Catalog" thành
// một MÀN HÌNH rồi Developer chép nguyên văn ra sidebar bản demo, nên một danh mục tên "Chức danh" đẻ ra
// đúng mục menu "Chức danh Catalog" — trong khi luật tên màn hình đã bắt tên phải ngắn và tiếng Anh.
//
// Đầy đủ ở docs/requirement-flow.md, mục "Ba cột TÊN của bảng đối tượng cũng là tiếng Anh".
public class BAChatEntityNamingRuleTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    // Đặc tả trường của bảng đối tượng nay nằm ở prompt riêng của bảng, chỉ nạp ở đúng lượt
    // InterviewTableGate mở cổng EntityMap — xem InterviewTablePromptTests.
    private const string EntityTablePromptKey = "BusinessAnalyst/table-entity-map.v1.md";
    private const string SpecPromptKey = "BusinessAnalyst/ai-design-spec.v1.md";
    private const string PocPromptKey = "Developer/poc-preview.v1.md";
    private const string VisualReviewPromptKey = "UiUx/poc-visual-review.v1.md";

    [Fact]
    public void EntityTablePrompt_NamesTheThreeEnglishColumns_AndKeepsTheRestVietnamese()
    {
        var prompt = ReadPrompt(EntityTablePromptKey);

        // Ba cột phải được GỌI TÊN. "Tên viết bằng tiếng Anh" chung chung thì model tự chọn cột nào là tên.
        Assert.Contains("`entity`, `fields[].name`, `states[].state`", prompt, StringComparison.Ordinal);

        // Dạng HIỂN THỊ, không phải định danh: chuỗi này còn là nhãn trên bảng người dùng đang rà.
        Assert.Contains("Title Case", prompt, StringComparison.Ordinal);
        Assert.Contains("effective_date", prompt, StringComparison.Ordinal);

        // Vế thứ hai của luật, và là vế dễ rơi nhất — người rà bảng là người nghiệp vụ.
        Assert.Contains("Tiếng Việt ở đâu:", prompt, StringComparison.Ordinal);
        foreach (var vietnameseCell in new[] { "`description`", "`meaning`", "`entryCondition`" })
        {
            Assert.Contains(vietnameseCell, prompt, StringComparison.Ordinal);
        }
    }

    // Ô ý nghĩa hết là phần thêm nếm từ lúc cột tên là tiếng Anh: nó là NỬA CÒN LẠI của dòng.
    [Fact]
    public void EntityTablePrompt_ForbidsAnEmptyMeaningNextToAnEnglishName()
    {
        var prompt = ReadPrompt(EntityTablePromptKey);

        Assert.Contains("`meaning` vì thế KHÔNG được để trống", prompt, StringComparison.Ordinal);
        Assert.Contains("một từ ngoại ngữ trơ trọi", prompt, StringComparison.Ordinal);
    }

    // Cột tên tiếng Anh cắt rời mối nối tới bảng cột của tài liệu nguồn ("Effective Date" không khớp "Ngày
    // hiệu lực"). `sourceColumn` là chỗ nối lại — và nó phải kèm luật CẤM BỊA, vì ô đó chở dấu "người dùng
    // đã chốt rồi".
    [Fact]
    public void EntityTablePrompt_AsksForTheSourceColumnVerbatim_AndForbidsMakingItUp()
    {
        var prompt = ReadPrompt(EntityTablePromptKey);

        Assert.Contains("`sourceColumn`", prompt, StringComparison.Ordinal);
        Assert.Contains("chép NGUYÊN VĂN tên cột của tài liệu nguồn", prompt, StringComparison.Ordinal);
        Assert.Contains("đừng điền cho có", prompt, StringComparison.Ordinal);
    }

    // Bước sinh spec chỉ CHÉP. Dịch lại ở đây là ba tầng sau (cột bảng dữ liệu, nhãn ô nhập, chip trạng
    // thái) đọc một bộ tên khác với bộ người dùng vừa tự tay rà.
    [Fact]
    public void SpecPrompt_CopiesTheConfirmedNamesInsteadOfTranslatingThem()
    {
        var prompt = ReadPrompt(SpecPromptKey);

        Assert.Contains("## 8. Data Model Summary", prompt, StringComparison.Ordinal);
        Assert.Contains("không dịch sang tiếng Việt", prompt, StringComparison.Ordinal);
        Assert.Contains("Effective Date", prompt, StringComparison.Ordinal);
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
