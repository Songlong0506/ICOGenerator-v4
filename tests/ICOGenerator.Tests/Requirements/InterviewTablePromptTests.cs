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

    // ── Lệnh "ĐỂ CUỐI, đừng hỏi lẻ" của hai nhóm chốt-bằng-bảng ─────────────────────────────────────
    //
    // Cùng bất biến "một việc, một đặc tả" như trên, cho một khối khác: lệnh cấm hỏi lẻ nhóm «Phân quyền
    // theo nghiệp vụ» / «Thông báo / nhắc nhở». Chỗ duy nhất của nó là hai hằng ĐIỀU KIỆN trong
    // BAChatPromptBlocks, KHÔNG phải prompt nền — và lần này lý do không chỉ là trôi lệch:
    //
    //  * Lệnh cấm là MỘT NHÁNH trạng thái của cổng (chưa tới lượt / bày bảng / đã chốt; nhóm thông báo còn
    //    nhánh thứ tư). Prompt nền vào MỌI lượt, nên một bản sao ở đó chọi thẳng với khối
    //    "## LƯỢT NÀY: BÀY BẢNG …" ở đúng lượt cổng mở, và với khối "bảng ĐÃ CHỐT" sau đó.
    //  * Nhóm thông báo có ĐƯỜNG THOÁT: dự án không có đối tượng nào mang trạng thái ⇒ bảng không bao giờ
    //    được bày ⇒ BAChatService gỡ khối cấm ra để nhóm quay về đường hỏi bằng câu hỏi. Bản sao vô điều
    //    kiện trong prompt nền làm đường thoát ấy chỉ tắt được một nửa: cơ chế thôi cấm, prompt vẫn cấm,
    //    nhóm kẹt [CHƯA HỎI] và nút "Write Requirement" không bao giờ sáng.
    //  * Bản sao đó đã bắt đầu trôi lệch thật: prompt nền có ngoại lệ "trừ orgUnit và nhân sự — đồng bộ từ
    //    COMPAS", hằng C# thì không. Nay ngoại lệ ấy nằm trong hằng, và prompt nền chỉ còn con trỏ.
    [Theory]
    [InlineData("mỗi vai trò được xem và thao tác những gì")]
    [InlineData("vai X còn được làm gì nữa không")]
    [InlineData("cứ vậy đã, có gì tôi bổ sung sau")]
    [InlineData("vai trò nào cần nhận email")]
    [InlineData("sự kiện nào cần gửi thông báo")]
    [InlineData("cả bốn nhóm")]
    public void DeferredBan_LivesInTheConditionalBlock_NotTheChatPrompt(string token)
    {
        var deferred = BAChatPromptBlocks.PermissionMatrixDeferred + "\n" + BAChatPromptBlocks.NotificationDeferred;

        Assert.Contains(token, deferred, StringComparison.Ordinal);
        Assert.DoesNotContain(token, ReadPrompt(ChatPromptKey), StringComparison.Ordinal);
    }

    // Vế "vẫn PHẢI hỏi như thường" đi theo lệnh cấm và phải nằm CÙNG chỗ với nó: cấm mà không nói rõ phần
    // nào còn phải hỏi thì model đọc thành "khỏi hỏi gì về hai nhóm này nữa" — mà quyền định hình LUỒNG và
    // các TRẠNG THÁI của đối tượng thì hoãn xuống cuối buổi là tự bịt mắt suốt cả buổi.
    [Fact]
    public void DeferredBan_NamesWhatStillMustBeAsked()
    {
        Assert.Contains("Vẫn PHẢI hỏi như thường", BAChatPromptBlocks.PermissionMatrixDeferred, StringComparison.Ordinal);
        Assert.Contains("LUỒNG", BAChatPromptBlocks.PermissionMatrixDeferred, StringComparison.Ordinal);
        Assert.Contains("COMPAS", BAChatPromptBlocks.PermissionMatrixDeferred, StringComparison.Ordinal);

        Assert.Contains("Vẫn PHẢI hỏi như thường", BAChatPromptBlocks.NotificationDeferred, StringComparison.Ordinal);
        Assert.Contains("TRẠNG THÁI", BAChatPromptBlocks.NotificationDeferred, StringComparison.Ordinal);
    }

    // Cái ở lại prompt nền: một con trỏ tới khối điều kiện, và bất biến "không có khối ⇒ hỏi như nhóm
    // thường" — vế thứ hai chính là đường thoát của ứng dụng danh mục thuần, nên nó phải nói ra thành lời.
    [Fact]
    public void ChatPrompt_KeepsOnlyThePointerToTheDeferredBlock()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("ĐỂ CUỐI, đừng hỏi lẻ", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Không có khối ấy trong ngữ cảnh ⇒ hỏi nhóm này như mọi nhóm khác",
            prompt,
            StringComparison.Ordinal);

        // Con trỏ chỉ đúng chỗ khi tiêu đề hai bên khớp nhau.
        foreach (var block in new[] { BAChatPromptBlocks.PermissionMatrixDeferred, BAChatPromptBlocks.NotificationDeferred })
        {
            var heading = block.Split('\n')[0].TrimStart('#', ' ');
            Assert.Contains(heading, prompt, StringComparison.Ordinal);
        }
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
