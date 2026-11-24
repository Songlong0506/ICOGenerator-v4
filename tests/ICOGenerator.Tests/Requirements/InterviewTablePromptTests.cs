using System.Reflection;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Khối "## LƯỢT NÀY: BÀY BẢNG …" của bảng luồng nạp từ file prompt riêng, chỉ đi vào ngữ cảnh ở đúng lượt
// cổng của nó mở (InterviewTableGate.Select); phần thiết kế (danh mục tác vụ, bản thiết kế UX, phân quyền)
// có prompt soạn ngầm riêng (ux-inventory / ux-blueprint / permission-matrix-draft), không qua lượt chat. Trước đó nửa LUẬT của chúng sống ở HAI chỗ —
// chuỗi C# trong BAChatPromptBlocks và một bản thứ hai trong requirement-chat.v4.md — và hai bản đã trôi
// lệch nhau đúng theo cách docs/llm-and-prompts.md đã cảnh báo: bản C# bắt model điền `evidence` cho từng
// bước bảng luồng và từng dòng bảng màn hình, hai trường KHÔNG tồn tại trên FlowMapStep và dòng bảng màn hình (nay đã gỡ) nên bị
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
        // nền cho cả năm bảng một lượt, nên tách ra thì mỗi file phải tự mang nó.
        Assert.Contains("`suggestions`", prompt, StringComparison.Ordinal);
        Assert.Contains("`questions`", prompt, StringComparison.Ordinal);
    }

    // Đặc tả trường của một bảng chỉ được sống ở MỘT chỗ. Mỗi token dưới đây là một ô đặc thù của đúng một
    // bảng; thấy nó ở hai file (hoặc quay lại prompt nền) nghĩa là bản sao thứ hai đã mọc lại.
    [Theory]
    [InlineData("flowSteps")]
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
    [InlineData(typeof(PermissionGrant), PermissionMatrixDraftService.PromptKey)]
    public void TablesWithEvidence_TeachHowToFillIt(Type row, string promptKey)
    {
        Assert.NotNull(row.GetProperty("Evidence", BindingFlags.Public | BindingFlags.Instance));
        Assert.Contains("`evidence`", ReadPrompt(promptKey), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BAChatPromptBlocks.FlowMapPromptKey, typeof(FlowMapRow), typeof(FlowMapStep))]
    [InlineData(UxDesignService.BlueprintPromptKey, typeof(UxScreen), typeof(UxAction))]
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

        Assert.Contains("mặc định KHÔNG trả về", prompt, StringComparison.Ordinal);
        Assert.Contains("## LƯỢT NÀY: BÀY BẢNG", prompt, StringComparison.Ordinal);

        // Ví dụ JSON của lượt thường không được liệt kê các khóa bảng nữa — chúng là nhiễu ở 90% số lượt,
        // và parser bỏ qua khóa thiếu (BAChatReplyParser dùng List<…>? + ?? new()).
        Assert.DoesNotContain("\"permissionMatrix\": []", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"flowMap\": []", prompt, StringComparison.Ordinal);
    }

    // ── Lệnh cấm hỏi lẻ của hai nhóm không-hỏi ───────────────────────────────────────────────────────
    //
    // Cùng bất biến "một việc, một đặc tả" như trên, cho một khối khác: lệnh cấm hỏi lẻ nhóm «Phân quyền
    // theo nghiệp vụ» / «Thông báo / nhắc nhở». Chỗ duy nhất của nó là hai hằng trong
    // BAChatPromptBlocks, KHÔNG phải prompt nền — và lý do không chỉ là trôi lệch:
    //
    //  * Lệnh cấm phân quyền là MỘT NHÁNH trạng thái (chưa có bảng / đã có bảng). Prompt nền vào MỌI lượt,
    //    nên một bản sao ở đó chọi thẳng với khối "bảng ĐÃ CHỐT". Lệnh cấm thông báo đứng cạnh nó, cùng
    //    khuôn "cấm gì / vẫn PHẢI hỏi gì" — xem doc của BAChatPromptBlocks.
    //  * Bản sao đó đã từng trôi lệch thật: prompt nền có ngoại lệ "trừ orgUnit và nhân sự — đồng bộ từ
    //    COMPAS", hằng C# thì không. Nay ngoại lệ ấy nằm trong hằng, và prompt nền chỉ còn con trỏ.
    [Theory]
    [InlineData("mỗi vai trò được xem và thao tác những gì")]
    [InlineData("vai X còn được làm gì nữa không")]
    [InlineData("cứ vậy đã, có gì tôi bổ sung sau")]
    [InlineData("vai trò nào cần nhận email")]
    [InlineData("sự kiện nào cần gửi thông báo")]
    [InlineData("ai cần được CC")]
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

    // Cái ở lại prompt nền: một con trỏ tới khối điều kiện, gọi đúng tiêu đề của nó.
    [Fact]
    public void ChatPrompt_KeepsOnlyThePointerToTheDeferredBlock()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("Phân quyền — đừng hỏi lẻ", prompt, StringComparison.Ordinal);

        // Con trỏ chỉ đúng chỗ khi tiêu đề hai bên khớp nhau.
        foreach (var block in new[] { BAChatPromptBlocks.PermissionMatrixDeferred, BAChatPromptBlocks.NotificationDeferred })
        {
            var heading = block.Split('\n')[0].TrimStart('#', ' ');
            Assert.Contains(heading, prompt, StringComparison.Ordinal);
        }
    }

    // ── Thiết kế màn hình là việc của BA ────────────────────────────────────────────────────────────
    //
    // Ca thật (dự án Locker): sau khi bảng màn hình đã chốt, người dùng thêm trạng thái Need Clean (FCM thu
    // hồi tủ ⇒ Need Clean, dọn xong bấm "Đã clean" ⇒ Available). Khối ngữ cảnh lúc đó cấm BA "thêm chức năng
    // ngoài các chức năng dưới đây", nên lối thoát duy nhất của BA là hỏi ngược *"Nút «Thu hồi tủ» và nút
    // «Đã clean» nên đặt ở màn hình nào?"* + ba chip tên màn hình — bắt người đi thuê BA tự thiết kế hộ.
    // Hai chỗ giữ cho câu đó không quay lại: luật ở prompt nền, và khối phạm vi màn hình đã chốt.
    [Fact]
    public void ChatPrompt_ForbidsAskingTheUserForScreenDesign()
    {
        var prompt = ReadPrompt(ChatPromptKey);

        Assert.Contains("Không hỏi người dùng về THIẾT KẾ GIAO DIỆN", prompt, StringComparison.Ordinal);
        Assert.Contains("nên đặt ở màn hình nào?", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedUxBlueprintBlock_ForbidsAskingPlacement_AndNoLongerForbidsNewFunctions()
    {
        Assert.Contains("KHÔNG hỏi người dùng", BAChatPromptBlocks.ConfirmedUxBlueprint, StringComparison.Ordinal);
        Assert.Contains("hỏi NGHIỆP VỤ", BAChatPromptBlocks.ConfirmedUxBlueprint, StringComparison.Ordinal);

        var block = UxBlueprintRenderer.RenderChatBlock(UxBlueprintBuilder.Serialize(new UxBlueprint
        {
            Tasks = { new UxTask { Id = "T-01", Name = "Thu hồi tủ", Placed = true } },
            Screens =
            {
                new UxScreen { Id = "S-01", Name = "Locker List", Actions = { new UxAction { Task = "T-01", Label = "Thu hồi tủ" } } }
            }
        }))!;

        // Bản thiết kế là của BA/UiUx: khối nói thẳng điều đó, để không tầng nào trích nó làm lời người dùng.
        Assert.Contains("KHÔNG phải lời người dùng", block, StringComparison.Ordinal);
        Assert.DoesNotContain("KHÔNG thêm chức năng", block, StringComparison.Ordinal);
        Assert.DoesNotContain("NGƯỜI DÙNG CHỐT", block, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> PromptFiles()
    {
        var keys = new[]
        {
            ChatPromptKey,
            BAChatPromptBlocks.FlowMapPromptKey,
            UxDesignService.InventoryPromptKey,
            UxDesignService.BlueprintPromptKey,
            PermissionMatrixDraftService.PromptKey,
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
