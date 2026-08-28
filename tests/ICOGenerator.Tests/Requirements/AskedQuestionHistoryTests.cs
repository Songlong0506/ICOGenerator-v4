using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Sổ "đã hỏi rồi" + phép thử trùng lặp. Bản đồ bao phủ chỉ có độ phân giải theo NHÓM (12 dòng), nên khi
// một dòng chưa đạt chuẩn [RÕ] — hoặc khi lượt chắt lọc bản đồ hỏng và bản đồ đứng im — model phát lại
// đúng câu hỏi mở đầu của nhóm đó, kèm chip gợi ý chính là câu trả lời người dùng vừa gõ. Lớp này là
// phanh tất định cho việc đó, nên các bất biến dưới đây là thứ giữ cho phanh không quá tay:
//   - bắt được câu cũ dù khác dấu câu/hoa thường, và câu cũ chỉ sửa vài chữ;
//   - KHÔNG chặn oan một câu hỏi mới về cùng chủ đề (hỏi tiếp phần "còn thiếu" là việc BA phải làm);
//   - KHÔNG chặn oan câu ngắn kiểu "Đúng không ạ?" — chúng lặp lại một cách hợp lệ;
//   - MIỄN trừ nhóm mà người dùng vừa bấm "chưa đúng?" — đó là lúc họ CHỦ ĐỘNG xin được hỏi lại.
public class AskedQuestionHistoryTests
{
    private static AgentConversation Assistant(string message, string[]? suggestions = null, params (string Group, string Question)[] questions) =>
        new()
        {
            Role = "assistant",
            Message = message,
            Suggestions = suggestions is { Length: > 0 } ? JsonSerializer.Serialize(suggestions) : null,
            Questions = questions.Length > 0
                ? JsonSerializer.Serialize(questions.Select(q => new BAChatQuestion { Group = q.Group, Question = q.Question }))
                : null
        };

    private static AgentConversation User(string message) => new() { Role = "user", Message = message };

    [Fact]
    public void Collect_TakesBatchQuestions_AndSingleQuestionTurns_ButNotSummariesOrFailures()
    {
        var turns = new List<AgentConversation>
        {
            Assistant("Mình hỏi nhanh mấy điểm sau nhé:", null,
                ("Đối tượng người dùng & vai trò", "Ai sẽ dùng app và vai trò của họ?"),
                ("Thông báo / nhắc nhở", "Khi có nhân viên đạt 11 giờ, cách nhắc nhở ra sao?")),
            User("Phòng bảo vệ xem dashboard, phòng nhân sự xem history"),
            Assistant("Quy trình hiện tại đang làm bằng gì?", new[] { "Excel", "Giấy" }),
            User("Excel"),
            // Lượt tóm tắt (không gợi ý) và lượt ⚠️ lỗi gọi AI đều KHÔNG phải câu hỏi.
            Assistant("Mình tóm tắt lại: app hiển thị nhân viên làm quá 11 giờ."),
            Assistant(ConversationTranscriptBuilder.LlmFailurePrefix + ", chưa thể trả lời. Chi tiết: timeout", new[] { "Thử lại" })
        };

        var asked = AskedQuestionHistory.Collect(turns);

        Assert.Equal(new[]
        {
            "Ai sẽ dùng app và vai trò của họ?",
            "Khi có nhân viên đạt 11 giờ, cách nhắc nhở ra sao?",
            "Quy trình hiện tại đang làm bằng gì?"
        }, asked);
    }

    // Câu MỞ (xin một lời KỂ) không được phép kèm chip — nên nếu sổ chỉ nhận lượt CÓ chip thì đúng loại
    // câu đắt nhất của buổi phỏng vấn không bao giờ vào sổ, và BA phát lại được nguyên văn. Ca thật (dự
    // án JD Libary, lượt 2 và lượt 4). Dấu hỏi là ranh giới: lượt tóm tắt vẫn đứng ngoài.
    [Fact]
    public void Collect_TakesOpenQuestionsWithNoChips()
    {
        var turns = new List<AgentConversation>
        {
            Assistant("Anh/chị kể giúp mình một lần gần nhất khi tạo và gán một JD cho nhân viên?"),
            User("HRBP làm trong 2 file excel"),
            Assistant("Mình ghi nhận: HRBP thao tác trên 2 file Excel.")
        };

        var asked = AskedQuestionHistory.Collect(turns);

        Assert.Equal(new[] { "Anh/chị kể giúp mình một lần gần nhất khi tạo và gán một JD cho nhân viên?" }, asked);
    }

    // Lượt bày BẢNG CHỐT đứng ngoài sổ: chỗ trả lời của nó là chính cái bảng, `Message` chỉ là câu dẫn —
    // mà câu dẫn của hai bảng khác nhau thì na ná nhau, để vào sổ là chặn oan lượt bày bảng kế tiếp.
    [Fact]
    public void Collect_SkipsTurnsThatCarryAConfirmationTable()
    {
        var withTable = Assistant("Anh/chị rà giúp mình bảng phân quyền dưới đây nhé?");
        withTable.PermissionMatrix = "[]";

        Assert.Empty(AskedQuestionHistory.Collect(new List<AgentConversation> { withTable }));
    }

    [Fact]
    public void IsRepeat_CatchesTheSameQuestionRegardlessOfPunctuationAndCase()
    {
        var keys = AskedQuestionHistory.Keys(new[] { "Ai sẽ sử dụng app này và vai trò của họ?" });

        Assert.True(AskedQuestionHistory.IsRepeat("ai sẽ sử dụng app này và vai trò của họ", keys));
        Assert.True(AskedQuestionHistory.IsRepeat("Ai sẽ sử dụng app này, và vai trò của họ?!", keys));
    }

    [Fact]
    public void IsRepeat_CatchesTheOldQuestionWithAFewWordsChanged()
    {
        // Ca thật đã gặp: BA hỏi lại đúng câu cũ, chỉ rụng một hai chữ. Nếu chỉ so khớp tuyệt đối thì
        // phanh này vô dụng — model gần như không bao giờ chép lại nguyên văn đến từng ký tự.
        var keys = AskedQuestionHistory.Keys(new[] { "Ai sẽ sử dụng app này và vai trò của họ?" });

        Assert.True(AskedQuestionHistory.IsRepeat("Ai sẽ dùng app và vai trò của họ?", keys));
    }

    [Fact]
    public void IsRepeat_DoesNotBlockAGenuineFollowUpOnTheSameTopic()
    {
        // Đây chính là việc BA PHẢI làm với một nhóm [MỘT PHẦN]: hỏi đúng phần còn thiếu. Chặn nó là
        // biến phanh chống-hỏi-lại thành phanh chống-phỏng-vấn.
        var keys = AskedQuestionHistory.Keys(new[] { "Ai sẽ sử dụng app này và vai trò của họ?" });

        Assert.False(AskedQuestionHistory.IsRepeat(
            "Trong hai phòng anh/chị vừa kể, ai là người chịu trách nhiệm gọi điện nhắc nhân viên?", keys));
    }

    // Ca thật (dự án JD Libary 4, lượt 16 → 20). Prompt bắt BA PHÁT LẠI điều đã ghi nhận trước khi hỏi, nên
    // một lượt hỏi một câu gần như luôn là "Cảm ơn anh/chị! Mình đã ghi nhận: … . <câu hỏi>?" — và phần phát
    // lại đổi theo từng lượt vì nó chép lời người dùng vừa nói. So cả `Message` là pha loãng đúng vế cần so:
    // hai lượt hỏi CÙNG một câu vẫn lệch nhau vì hai câu phát lại khác nhau, và phanh câm ở đúng chỗ prompt
    // làm đúng nhất. Lượt 20 thoát được phanh, hỏi lại nguyên câu của lượt 16, kèm một chip chép lại đúng
    // câu trả lời người dùng vừa gõ ở lượt 19.
    [Fact]
    public void IsRepeat_SeesThroughTheRecapPreambleThatThePromptRequires()
    {
        var keys = AskedQuestionHistory.Keys(new[]
        {
            "Cảm ơn anh/chị! Mình đã ghi nhận: Manager tự quản lý JD của orgUnit mình. Vậy khi JD đã available "
            + "và được gán cho nhân viên, nếu cần chỉnh sửa hoặc ngừng sử dụng JD đó thì xử lý như thế nào?"
        });

        Assert.True(AskedQuestionHistory.IsRepeat(
            "Cảm ơn anh/chị! Mình đã ghi nhận: JD đã được HoD approve thì không sửa trực tiếp được, muốn chỉnh "
            + "sửa thì phải upgrade version (giữ nguyên mã JD cũ, tăng version 1, 2, 3...) và version mới phải "
            + "trải qua quy trình approve lại từ đầu. Mình còn một điểm cần làm rõ: khi JD đã available và được "
            + "gán cho nhân viên, nếu cần chỉnh sửa thì xử lý thế nào?", keys));
    }

    // Chiều ngược lại, cùng buổi phỏng vấn đó: lượt 16 hỏi một câu KÉP ("chỉnh sửa HOẶC ngừng sử dụng") mà
    // người dùng chỉ trả lời được một nửa, nên lượt 18 đi nhặt lại nửa còn lại. Đó đúng là việc BA phải làm —
    // chặn nó là biến phanh chống-hỏi-lại thành phanh chống-phỏng-vấn, và ở ca thật thì lượt bị chặn chính là
    // lượt đắt nhất của buổi (câu trả lời của nó chở nguyên luật upgrade version).
    [Fact]
    public void IsRepeat_DoesNotBlockTheFollowUpOnTheHalfOfADoubleQuestionThatWentUnanswered()
    {
        var keys = AskedQuestionHistory.Keys(new[]
        {
            "Cảm ơn anh/chị! Mình đã ghi nhận: Manager tự quản lý JD của orgUnit mình. Vậy khi JD đã available "
            + "và được gán cho nhân viên, nếu cần chỉnh sửa hoặc ngừng sử dụng JD đó thì xử lý như thế nào?"
        });

        Assert.False(AskedQuestionHistory.IsRepeat(
            "Cảm ơn anh/chị! Mình đã ghi nhận: khi ngừng sử dụng JD thì không gán mới nữa nhưng vẫn giữ lịch "
            + "sử. Vậy khi JD đã gán cho nhân viên mà cần chỉnh sửa thì xử lý thế nào?", keys));
    }

    // Vế hỏi quá ngắn ⇒ GIỮ NGUYÊN cả message. Hai lượt khác hẳn nhau vẫn có thể cùng kết bằng "Đúng không
    // ạ?"; cắt xuống còn bấy nhiêu là dựng ra một vụ trùng khoá TUYỆT ĐỐI giữa hai lượt không liên quan.
    [Fact]
    public void QuestionCore_KeepsTheWholeMessage_WhenTheQuestionClauseIsTooShortToStandAlone()
    {
        var keys = AskedQuestionHistory.Keys(new[]
        {
            "Mình đang ghi nhận: Manager tạo JD cho orgUnit của mình. Đúng không ạ?"
        });

        Assert.False(AskedQuestionHistory.IsRepeat(
            "Mình đang ghi nhận: HRBP duyệt trước rồi HoD duyệt sau. Đúng không ạ?", keys));
    }

    [Fact]
    public void QuestionCore_LeavesTextWithoutAQuestionUntouched()
    {
        Assert.Equal("Đối tượng người dùng & vai trò", AskedQuestionHistory.QuestionCore("Đối tượng người dùng & vai trò"));
        Assert.Equal(string.Empty, AskedQuestionHistory.QuestionCore(null));
    }

    [Fact]
    public void IsRepeat_ShortConfirmationsAreOnlyMatchedExactly()
    {
        var keys = AskedQuestionHistory.Keys(new[] { "Đúng vậy không ạ?" });

        Assert.True(AskedQuestionHistory.IsRepeat("đúng vậy không ạ", keys));
        // Hai câu ngắn khác nhau không được coi là một chỉ vì dùng chung vài từ.
        Assert.False(AskedQuestionHistory.IsRepeat("Còn gì nữa không ạ?", keys));
    }

    [Fact]
    public void IsRepeat_EmptyHistoryOrEmptyQuestion_IsNeverARepeat()
    {
        Assert.False(AskedQuestionHistory.IsRepeat("Ai sẽ dùng app này?", new HashSet<string>()));
        Assert.False(AskedQuestionHistory.IsRepeat("   ", AskedQuestionHistory.Keys(new[] { "Ai sẽ dùng app này?" })));
    }

    [Fact]
    public void ReopenedGroups_ExemptsTheGroupTheUserJustFlaggedAsWrong()
    {
        // Người dùng nói trong chat "nhóm này BA hiểu chưa đúng" ⇒ lượt chắt lọc hạ dòng đó xuống
        // [MỘT PHẦN] kèm ghi chú. Không có ngoại lệ này, phanh sẽ chặn đúng cái đường thoát vừa mở ra.
        var map = "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: "
                  + AskedQuestionHistory.ReopenNote + " — cần hỏi lại và chốt lại.\n"
                  + "- Thông báo / nhắc nhở: [MỘT PHẦN] còn thiếu: khi nào gửi";

        var reopened = AskedQuestionHistory.ReopenedGroups(CoverageMapParser.Parse(map));

        Assert.True(AskedQuestionHistory.IsExempt(
            new BAChatQuestion { Group = "Đối tượng người dùng & vai trò", Question = "Ai sẽ dùng app?" }, reopened));
        Assert.False(AskedQuestionHistory.IsExempt(
            new BAChatQuestion { Group = "Thông báo / nhắc nhở", Question = "Ai cần được báo?" }, reopened));
    }

    [Fact]
    public void BuildNote_ListsRecentQuestions_AndIsEmptyWhenNothingWasAsked()
    {
        Assert.Equal(string.Empty, AskedQuestionHistory.BuildNote(Array.Empty<string>()));

        var many = Enumerable.Range(1, AskedQuestionHistory.MaxQuestionsInNote + 5)
            .Select(i => $"Câu hỏi số {i}?")
            .ToList();

        var note = AskedQuestionHistory.BuildNote(many);

        // Giữ các câu GẦN NHẤT (câu cũ hơn đã nằm trong bộ nhớ tóm tắt), và không phình quá trần.
        Assert.Contains($"Câu hỏi số {many.Count}?", note);
        Assert.DoesNotContain("Câu hỏi số 1?", note);
        Assert.Equal(AskedQuestionHistory.MaxQuestionsInNote, note.Split("\n- ").Length - 1);
    }
}
