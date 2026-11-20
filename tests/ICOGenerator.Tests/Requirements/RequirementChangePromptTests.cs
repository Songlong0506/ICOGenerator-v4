using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Localization;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Prompt của chế độ yêu cầu thay đổi (requirement-change.v1.md) không đi kèm bản đồ bao phủ, nên trường
// `group` của thẻ gộp chỉ có danh sách nhãn TRONG prompt để chép. Nhãn lệch một chữ là KeyOf trả null và
// câu hỏi rơi khỏi mọi cổng theo nhóm — lỗi câm, không log.
public class RequirementChangePromptTests
{
    private static string Prompt() => PromptFixture.Read(BAChatService.ChangeRequestPromptKey);

    [Fact]
    public void ThePromptListsEveryCoverageGroupLabel_Verbatim()
    {
        var prompt = Prompt();

        foreach (var key in CoverageGroupKey.All)
            Assert.Contains("«" + Loc.In(Loc.DefaultLanguage, "CoverageGroup.Label." + key) + "»", prompt, StringComparison.Ordinal);
    }

    // Tên các khối mà prompt nhắc tới phải khớp heading do code dựng — không thì BA được dặn đọc một khối
    // không tồn tại.
    [Fact]
    public void ThePromptNamesTheBlocksTheCodeActuallySends()
    {
        var prompt = Prompt();

        Assert.Contains("## Product Brief đã duyệt", BAChatPromptBlocks.ApprovedBrief("V1", "x"), StringComparison.Ordinal);
        Assert.Contains("\"## Product Brief đã duyệt\"", prompt, StringComparison.Ordinal);
        Assert.StartsWith("## Các màn hình hiện có của ứng dụng", BAChatPromptBlocks.ScreenIndex("x"), StringComparison.Ordinal);
        Assert.Contains("\"## Các màn hình hiện có của ứng dụng\"", prompt, StringComparison.Ordinal);
        Assert.StartsWith("## Bộ nhớ hội thoại", BAChatPromptBlocks.ConversationMemory("x"), StringComparison.Ordinal);
        Assert.StartsWith("## Điểm cần làm rõ còn tồn đọng", BAChatPromptBlocks.OpenQuestions(
            new[] { new OpenQuestionEntry { Text = "x" } }), StringComparison.Ordinal);
    }

    // Ba luật cấm hỏi mà ở chế độ phỏng vấn do các khối bảng chở (phân quyền, thông báo, giao diện) nay chỉ
    // còn nằm trong prompt — thiếu là BA hỏi lại đúng thứ hệ thống tự soạn.
    [Fact]
    public void ThePromptCarriesTheRulesTheDroppedTableBlocksUsedToCarry()
    {
        var prompt = Prompt();

        Assert.Contains("**Phân quyền**", prompt, StringComparison.Ordinal);
        Assert.Contains("**Thông báo/email**", prompt, StringComparison.Ordinal);
        Assert.Contains("**Thiết kế giao diện**", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenIndex_IsOneLinePerIncludedScreen_WithoutFunctions()
    {
        var json = JsonSerializer.Serialize(new List<ScreenScopeRow>
        {
            new() { Screen = "Locker List", Purpose = "Quản lý tủ", ConfirmedByUser = true,
                Functions = { new ScreenFunction { Name = "Thêm tủ mới", ConfirmedByUser = true } } },
            new() { Screen = "Area List", ConfirmedByUser = false },
            new() { Screen = "Old Screen", Included = false, ConfirmedByUser = true },
        });

        var index = ScreenScopeMapBuilder.RenderScreenIndex(json);

        Assert.Equal("* Locker List — Quản lý tủ\n* Area List (mới lộ ra, đang soạn)", index?.ReplaceLineEndings("\n"));
        Assert.Null(ScreenScopeMapBuilder.RenderScreenIndex(null));
    }
}
