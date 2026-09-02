using System.Reflection;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Sáu khối "## LƯỢT NÀY: BÀY BẢNG …" nay nạp từ sáu file prompt riêng, mỗi file chỉ đi vào ngữ cảnh ở
// đúng lượt cổng của nó mở (InterviewTableGate.Select). Trước đó nửa LUẬT của chúng sống ở HAI chỗ —
// chuỗi C# trong BAChatPromptBlocks và một bản thứ hai trong requirement-chat.v4.md — và hai bản đã trôi
// lệch nhau đúng theo cách docs/llm-and-prompts.md đã cảnh báo: bản C# bắt model điền `evidence` cho từng
// bước bảng luồng và từng dòng bảng màn hình, hai trường KHÔNG tồn tại trên FlowStep/ScreenScopeRow nên bị
// bỏ lúc parse, còn bản prompt lại nói đúng. Bộ test này giữ ba thứ:
//
//  1. Mỗi đặc tả trường chỉ còn MỘT bản, và nó không quay lại prompt nền.
//  2. Prompt của một bảng chỉ bắt điền `evidence` khi contract của bảng đó THẬT SỰ có trường ấy.
//  3. Prompt nền vẫn giữ nguyên bất biến "không có khối LƯỢT NÀY ⇒ không dựng bảng nào" — thứ duy nhất
//     còn lại ở đó sau khi cắt, và cũng là thứ giữ cho lượt chat thường không tự đẻ ra bảng.
public class InterviewTablePromptTests
{
    private const string ChatPromptKey = "BusinessAnalyst/requirement-chat.v4.md";

    public static TheoryData<string, string, string> TablePrompts() => new()
    {
        { BAChatPromptBlocks.FlowMapPromptKey, "BÀY BẢNG LUỒNG NGHIỆP VỤ", "flowMap" },
        { BAChatPromptBlocks.ScreenScopePromptKey, "BẢNG MÀN HÌNH", "screenScopeMap" },
        { BAChatPromptBlocks.EntityMapPromptKey, "BÀY BẢNG ĐỐI TƯỢNG NGHIỆP VỤ", "entityMap" },
        { BAChatPromptBlocks.ReportMapPromptKey, "BÀY BẢNG BÁO CÁO", "reportMap" },
        { BAChatPromptBlocks.PermissionMatrixPromptKey, "BÀY BẢNG PHÂN QUYỀN", "permissionMatrix" },
        { BAChatPromptBlocks.NotificationMapPromptKey, "BÀY BẢNG THÔNG BÁO", "notificationMap" },
    };

    [Theory]
    [MemberData(nameof(TablePrompts))]
    public void EachTablePrompt_CarriesItsOwnTurnBlockAndField(string promptKey, string heading, string field)
    {
        var prompt = ReadPrompt(promptKey);

        Assert.Contains("## LƯỢT NÀY:", prompt, StringComparison.Ordinal);
        Assert.Contains(heading, prompt, StringComparison.Ordinal);
        Assert.Contains("`" + field + "`", prompt, StringComparison.Ordinal);

        // Lượt bày bảng là lượt KHÔNG có chip: bảng là chỗ trả lời duy nhất. Luật này trước đây nằm ở prompt
        // nền cho cả sáu bảng một lượt, nên tách ra thì mỗi file phải tự mang nó.
        Assert.Contains("`suggestions`", prompt, StringComparison.Ordinal);
        Assert.Contains("`questions`", prompt, StringComparison.Ordinal);
    }

    // Đặc tả trường của một bảng chỉ được sống ở MỘT chỗ. Mỗi token dưới đây là một ô đặc thù của đúng một
    // bảng; thấy nó ở hai file (hoặc quay lại prompt nền) nghĩa là bản sao thứ hai đã mọc lại.
    [Theory]
    [InlineData("breakdown")]
    [InlineData("parentEntity")]
    [InlineData("flowSteps")]
    [InlineData("entryCondition")]
    [InlineData("sourceColumn")]
    [InlineData("grants")]
    public void FieldSpec_LivesInExactlyOnePrompt(string token)
    {
        var owners = PromptFiles()
            .Where(f => File.ReadAllText(f.Value).Contains(token, StringComparison.Ordinal))
            .Select(f => f.Key)
            .ToList();

        Assert.Single(owners);
        Assert.DoesNotContain(ChatPromptKey, owners);
    }

    // Bảng có `Evidence` trên contract thì prompt PHẢI dạy cách điền; bảng không có thì prompt phải nói
    // thẳng là không có, vì "im lặng" chính là chỗ bản C# cũ tự dựng ra một trường không tồn tại.
    [Theory]
    [InlineData(typeof(EntityMapRow), BAChatPromptBlocks.EntityMapPromptKey)]
    [InlineData(typeof(PermissionGrant), BAChatPromptBlocks.PermissionMatrixPromptKey)]
    [InlineData(typeof(NotificationMapRow), BAChatPromptBlocks.NotificationMapPromptKey)]
    public void TablesWithEvidence_TeachHowToFillIt(Type row, string promptKey)
    {
        Assert.NotNull(row.GetProperty("Evidence", BindingFlags.Public | BindingFlags.Instance));
        Assert.Contains("`evidence`", ReadPrompt(promptKey), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BAChatPromptBlocks.FlowMapPromptKey, typeof(FlowMapRow), typeof(FlowStep))]
    [InlineData(BAChatPromptBlocks.ScreenScopePromptKey, typeof(ScreenScopeRow), typeof(ScreenFunction))]
    [InlineData(BAChatPromptBlocks.ReportMapPromptKey, typeof(ReportMapRow), typeof(ReportMapRow))]
    public void TablesWithoutEvidence_SayThatOutLoud(string promptKey, Type row, Type child)
    {
        Assert.Null(row.GetProperty("Evidence", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(child.GetProperty("Evidence", BindingFlags.Public | BindingFlags.Instance));

        Assert.Contains("KHÔNG có trường `evidence`", ReadPrompt(promptKey), StringComparison.Ordinal);
    }

    // Thứ DUY NHẤT còn lại ở prompt nền sau khi cắt: không có khối "LƯỢT NÀY" thì không dựng bảng nào.
    [Fact]
    public void ChatPrompt_KeepsOnlyThePointerToTheTurnBlock()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("mặc định KHÔNG trả về trường nào trong số đó", prompt, StringComparison.Ordinal);
        Assert.Contains("## LƯỢT NÀY: BÀY BẢNG", prompt, StringComparison.Ordinal);

        // Ví dụ JSON của lượt thường không được liệt kê các khóa bảng nữa — chúng là nhiễu ở 90% số lượt,
        // và parser bỏ qua khóa thiếu (BAChatReplyParser dùng List<…>? + ?? new()).
        Assert.DoesNotContain("\"permissionMatrix\": []", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"flowMap\": []", prompt, StringComparison.Ordinal);
    }

    // ── Bộ đọc phần của template nhiều hình dạng ────────────────────────────────────────────────────

    private const string TwoShapes = """
        # shape:first
        Lời mở đầu A
        ## LƯỢT NÀY: BÀY BẢNG MÀN HÌNH

        # shape:reshow
        Lời mở đầu B

        # rules
        Luật dùng chung
        """;

    [Fact]
    public void Section_ReturnsOnlyTheRequestedShape()
    {
        Assert.Contains("Lời mở đầu A", BAChatPromptBlocks.Section(TwoShapes, BAChatPromptBlocks.FirstShapeSection));
        Assert.DoesNotContain("Lời mở đầu B", BAChatPromptBlocks.Section(TwoShapes, BAChatPromptBlocks.FirstShapeSection));
        Assert.DoesNotContain("Luật dùng chung", BAChatPromptBlocks.Section(TwoShapes, BAChatPromptBlocks.FirstShapeSection));

        Assert.Equal("Lời mở đầu B", BAChatPromptBlocks.Section(TwoShapes, BAChatPromptBlocks.ReshowShapeSection));
        Assert.Equal("Luật dùng chung", BAChatPromptBlocks.Section(TwoShapes, BAChatPromptBlocks.RulesSection));
    }

    // Tiêu đề cấp 2 trong thân là nội dung prompt, không phải dấu phân phần.
    [Fact]
    public void Section_TreatsOnlyLevelOneHeadingsAsMarkers()
        => Assert.Contains(
            "## LƯỢT NÀY: BÀY BẢNG MÀN HÌNH",
            BAChatPromptBlocks.Section(TwoShapes, BAChatPromptBlocks.FirstShapeSection),
            StringComparison.Ordinal);

    // Fail-open: ai đó dán đè một bản phẳng ở Prompt Studio thì model vẫn nhận đủ LUẬT, chỉ mất lời mở đầu.
    [Fact]
    public void Section_FallsBackToTheWholeTemplateWhenMarkersAreGone()
    {
        const string flat = "## LƯỢT NÀY: BÀY BẢNG MÀN HÌNH\nLuật dùng chung";

        Assert.Equal(flat, BAChatPromptBlocks.Section(flat, BAChatPromptBlocks.RulesSection));
        Assert.Equal(string.Empty, BAChatPromptBlocks.Section(flat, BAChatPromptBlocks.FirstShapeSection));
        Assert.Equal(string.Empty, BAChatPromptBlocks.Section(null, BAChatPromptBlocks.FirstShapeSection));
    }

    // Bảng màn hình là bảng DUY NHẤT có hai lời mở đầu loại trừ nhau; file thật phải mang đủ cả ba phần,
    // nếu không lượt bày lại sẽ nói "anh/chị chưa bao giờ thấy danh sách này" với bảng họ vừa tự tay rà.
    [Fact]
    public void ScreenScopePrompt_ShipsBothShapesAndSharedRules()
    {
        var template = ReadPrompt(BAChatPromptBlocks.ScreenScopePromptKey);

        var first = BAChatPromptBlocks.Section(template, BAChatPromptBlocks.FirstShapeSection);
        var reshow = BAChatPromptBlocks.Section(template, BAChatPromptBlocks.ReshowShapeSection);
        var rules = BAChatPromptBlocks.Section(template, BAChatPromptBlocks.RulesSection);

        Assert.NotEmpty(first);
        Assert.NotEmpty(reshow);
        Assert.NotEmpty(rules);
        Assert.NotEqual(first, reshow);

        Assert.Contains("chưa bao giờ nhìn thấy", first, StringComparison.Ordinal);
        Assert.Contains("đã tự tay rà và CHỐT", reshow, StringComparison.Ordinal);

        // Bộ luật trường chỉ có MỘT bản, dùng chung cho cả hai lời mở đầu.
        Assert.Contains("`flowSteps`", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("`flowSteps`", first, StringComparison.Ordinal);
        Assert.DoesNotContain("`flowSteps`", reshow, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenScopeTable_PutsTheChosenShapeAheadOfTheRulesAndTheData()
    {
        var block = BAChatPromptBlocks.ScreenScopeTable(
            ReadPrompt(BAChatPromptBlocks.ScreenScopePromptKey),
            reshow: false,
            effectiveScreens: new[] { "Training Plan" },
            pendingScreens: Array.Empty<string>(),
            pendingFunctions: Array.Empty<string>(),
            flowMapJson: null);

        Assert.StartsWith("## LƯỢT NÀY: BÀY BẢNG MÀN HÌNH", block, StringComparison.Ordinal);
        Assert.Contains("`flowSteps`", block, StringComparison.Ordinal);
        Assert.Contains("- Training Plan", block, StringComparison.Ordinal);
        Assert.DoesNotContain("đã tự tay rà và CHỐT", block, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> PromptFiles()
    {
        var keys = new[]
        {
            ChatPromptKey,
            BAChatPromptBlocks.FlowMapPromptKey,
            BAChatPromptBlocks.ScreenScopePromptKey,
            BAChatPromptBlocks.EntityMapPromptKey,
            BAChatPromptBlocks.ReportMapPromptKey,
            BAChatPromptBlocks.PermissionMatrixPromptKey,
            BAChatPromptBlocks.NotificationMapPromptKey,
        };

        return keys.ToDictionary(k => k, PromptPath, StringComparer.Ordinal);
    }

    // Cùng cách tìm Prompts/ như BAChatScopeConflictRuleTests: ưu tiên bản copy trong bin, không có thì đi
    // ngược lên repo root.
    private static string PromptPath(string promptKey)
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

        throw new FileNotFoundException("Không tìm thấy prompt " + promptKey + " từ " + AppContext.BaseDirectory);
    }

    private static string ReadPrompt(string promptKey) => File.ReadAllText(PromptPath(promptKey));
}
