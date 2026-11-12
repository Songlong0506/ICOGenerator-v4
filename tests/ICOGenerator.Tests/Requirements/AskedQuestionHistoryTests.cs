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
            Assistant(RequirementScreenText.LlmFailurePrefix + ", chưa thể trả lời. Chi tiết: timeout", new[] { "Thử lại" })
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
    public void IsRepeat_DoesNotBlockAGenuineFollowUpOnTheSameTopic()
    {
        // Đây chính là việc BA PHẢI làm với một nhóm [MỘT PHẦN]: hỏi đúng phần còn thiếu. Chặn nó là
        // biến phanh chống-hỏi-lại thành phanh chống-phỏng-vấn.
        var keys = AskedQuestionHistory.Keys(new[] { "Ai sẽ sử dụng app này và vai trò của họ?" });

        Assert.False(AskedQuestionHistory.IsRepeat(
            "Trong hai phòng anh/chị vừa kể, ai là người chịu trách nhiệm gọi điện nhắc nhân viên?", keys));
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
        var bullets = "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: " + AskedQuestionHistory.ReopenNote
            + " — cần hỏi lại và chốt lại.\n"
            + "- Thông báo / nhắc nhở: [MỘT PHẦN] còn thiếu: khi nào gửi";

        // Cụm tín hiệu nay nằm trong CÂU HỎI của nhóm, nên bản đồ phải được gắn câu hỏi vào mới đọc ra được.
        var reopened = AskedQuestionHistory.ReopenedGroups(CoverageMapParser.AttachQuestions(
            CoverageMapParser.Parse(CoverageMapFixture.Map(bullets)), CoverageMapFixture.Questions(bullets)));

        Assert.True(AskedQuestionHistory.IsExempt(
            new BAChatQuestion { Group = "Đối tượng người dùng & vai trò", Question = "Ai sẽ dùng app?" }, reopened));
        Assert.False(AskedQuestionHistory.IsExempt(
            new BAChatQuestion { Group = "Thông báo / nhắc nhở", Question = "Ai cần được báo?" }, reopened));
    }

    // CA THẬT (dự án quản lý khóa học bắt buộc — 2026-09-03, hai lượt BA liền nhau). Khuôn
    // "<ai đó> sẽ dùng ứng dụng để làm những việc gì? Ví dụ: A, B, hay còn thao tác nào khác?" đặt CHỦ THỂ
    // của câu hỏi ở câu TRƯỚC, còn mệnh đề vét chỉ là đuôi của danh sách ví dụ — một văn mẫu dùng lại cho
    // mọi chủ thể. BA hỏi xong vai Quản lý trực tiếp rồi hỏi sang vai Nhân viên, một câu hoàn toàn mới, và
    // bị phanh chặn vì hai lượt trùng đúng cái đuôi ấy. Lượt đó bị thay bằng câu chặn của cổng và vai Nhân
    // viên không được hỏi lượt nào.
    private const string AskedManagerRole =
        "Cảm ơn anh/chị. Mình ghi nhận: Admin được người quản trị hệ thống chỉ định sẵn. Bây giờ mình muốn "
        + "làm rõ thêm về vai trò của Quản lý trực tiếp trong ứng dụng. Anh/chị cho mình biết: Quản lý trực "
        + "tiếp sẽ dùng ứng dụng để làm những việc gì? Ví dụ: xem danh sách nhân viên và khóa học bắt buộc "
        + "của họ, hay còn thao tác nào khác?";

    // CA THẬT (dự án Locker management 7, lượt 88 → lượt kế — AI Call Log BAChat 2026-11-11 09:04 UTC).
    // Model trả một nhịp tóm tắt kiểm chứng hợp lệ, kết bằng "Anh/chị xác nhận cách hiểu này đúng chưa
    // ạ?". Ba lượt trước đó BA đã chốt một KỊCH BẢN khác bằng "Anh/chị xác nhận đúng cách hiểu này không
    // ạ?". Hai vế hỏi chung 9/10 từ ⇒ phanh coi bản tóm tắt là hỏi lại và thay nó bằng câu chặn dựng sẵn
    // của cổng. Cả hai câu đều chỉ xin một tiếng gật cho đoạn đứng trước nó, nên khoá phải là CẢ lượt.
    private const string ScenarioConfirmTurn =
        "Cảm ơn anh/chị. Mình chốt vậy nhé: nhân viên chọn khu vực mong muốn khi xin tủ; khi yêu cầu tới "
        + "FCM, FCM có thể chọn tủ trống trong khu vực đó hoặc chọn tủ ở khu vực khác. Anh/chị xác nhận "
        + "đúng cách hiểu này không ạ?";

    private const string SummaryCheckTurn =
        "Cảm ơn anh/chị. Mình tóm tắt lại cách hiểu các ý chính để anh/chị kiểm tra: nhân viên xin tủ qua "
        + "quản lý trực tiếp duyệt rồi FCM duyệt và cấp tủ; tủ vĩnh viễn hoặc tạm thời (ngày to sau ngày "
        + "from, không giới hạn số ngày); từ chối ở bất kỳ bước nào thì hủy hẳn; trạng thái tủ Available, "
        + "Inused, Damaged. Anh/chị xác nhận cách hiểu này đúng chưa ạ?";

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // HAI PHÍA CỦA PHANH PHẢI DÙNG CHUNG MỘT PHÉP THỬ "lượt này có HỎI không"
    //
    // Phía ghi sổ nhận diện bằng "có chip HOẶC có dấu hỏi", phía đối chiếu từng dùng "có chip HOẶC là câu
    // mở" — mà cờ câu-mở chỉ bật khi câu chứa một cụm xin-kể. Cả một lớp câu hỏi (không chip, không cụm
    // xin-kể) vì thế vào được sổ nhưng không bao giờ bị soi lại. Ca thật: chính câu vét ở trên.
    [Fact]
    public void IsAskingTurn_CountsAChiplessQuestion_ButNotASummary()
    {
        Assert.True(AskedQuestionHistory.IsAskingTurn(
            "Vậy ngoài việc khóa học hết hạn, còn có trường hợp nào khác cần xử lý không?", false));
        Assert.True(AskedQuestionHistory.IsAskingTurn("Mình đã ghi nhận đủ rồi.", true));
        Assert.False(AskedQuestionHistory.IsAskingTurn("Mình tóm tắt lại: app theo dõi hạn hiệu lực.", false));
        Assert.False(AskedQuestionHistory.IsAskingTurn(null, false));
    }

}
