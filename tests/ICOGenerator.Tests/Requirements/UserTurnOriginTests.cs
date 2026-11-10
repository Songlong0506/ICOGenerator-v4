using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;
using Kind = ICOGenerator.Services.Requirements.UserTurnOrigin.Kind;

namespace ICOGenerator.Tests.Requirements;

// Nguồn gốc lượt người dùng cho bộ nhớ cấp user. Chốt: chip (chọn một / chọn nhiều, kể cả chip tự chứa
// dấu phẩy), thẻ hỏi gộp, bảng chốt được nhận ra; mọi thứ khác — gõ khác chip, không có lượt BA trước,
// lượt trước không có gợi ý — rơi về TỰ GÕ (phía an toàn: giữ hành vi như trước khi có lớp này).
public class UserTurnOriginTests
{
    private static AgentConversation Ba(string[]? chips = null, bool multi = false) => new()
    {
        Role = "assistant",
        Message = "Câu hỏi?",
        Suggestions = chips == null ? null : JsonSerializer.Serialize(chips),
        SuggestionsMultiSelect = multi
    };

    private static AgentConversation User(string message) => new() { Role = "user", Message = message };

    [Fact]
    public void ExactChip_IsPickedSuggestion()
    {
        Assert.Equal(Kind.PickedSuggestion,
            UserTurnOrigin.Classify(User("Đúng như vậy"), Ba(new[] { "Đúng như vậy", "Không, mình muốn khác" })));
    }

    [Fact]
    public void TextDifferentFromChips_IsTyped()
    {
        Assert.Equal(Kind.Typed,
            UserTurnOrigin.Classify(User("Không, FCM tự duyệt luôn"), Ba(new[] { "Đúng như vậy", "Không, mình muốn khác" })));
    }

    [Fact]
    public void MultiSelect_JoinedChips_IncludingChipWithComma_IsPickedSuggestion()
    {
        var ba = Ba(new[] { "HR", "Manager, Supervisor", "FCM" }, multi: true);
        Assert.Equal(Kind.PickedSuggestion, UserTurnOrigin.Classify(User("HR, Manager, Supervisor, FCM"), ba));
        Assert.Equal(Kind.Typed, UserTurnOrigin.Classify(User("HR, Bảo vệ"), ba));
    }

    [Fact]
    public void JoinedChips_WhenNotMultiSelect_IsTyped()
    {
        Assert.Equal(Kind.Typed,
            UserTurnOrigin.Classify(User("HR, FCM"), Ba(new[] { "HR", "FCM" }, multi: false)));
    }

    [Fact]
    public void BatchCardAnswer_IsAnsweredBatchCard()
    {
        var ba = Ba();
        ba.Questions = JsonSerializer.Serialize(new[]
        {
            new BAChatQuestion { Question = "Ai duyệt?", Suggestions = new List<string> { "Manager" } },
            new BAChatQuestion { Question = "Bao lâu?", Suggestions = new List<string> { "1 ngày" } }
        });
        Assert.Equal(Kind.AnsweredBatchCard,
            UserTurnOrigin.Classify(User("- Ai duyệt?: Manager\n- Bao lâu?: 1 ngày"), ba));
    }

    [Fact]
    public void TableSubmission_AfterTableTurn_IsSubmittedTable_ButNotWithoutTable()
    {
        var withTable = Ba();
        withTable.ScreenScopeMap = "[{}]";
        const string msg = "Mình đã rà bảng màn hình, đây là các màn hình ứng dụng cần có:\n- Locker";

        Assert.Equal(Kind.SubmittedTable, UserTurnOrigin.Classify(User(msg), withTable));
        Assert.Equal(Kind.Typed, UserTurnOrigin.Classify(User(msg), Ba()));
    }

    [Fact]
    public void NoPreviousBaTurn_IsTyped()
    {
        Assert.Equal(Kind.Typed, UserTurnOrigin.Classify(User("Đúng như vậy"), null));
        Assert.Equal(Kind.Typed, UserTurnOrigin.Classify(User("Đúng như vậy"), User("Đúng như vậy")));
    }
}
