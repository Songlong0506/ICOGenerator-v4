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

    // CHIP ĐÃ BÀY MÀ KHÔNG CHỌN cũng là một câu trả lời — "cái này thì không". Sổ câu hỏi không thấy điều
    // đó (nó chỉ ghi CÂU HỎI), nên một câu có/không hỏi riêng đúng chip vừa bị bỏ lọt qua phanh trên.
    //
    // Ca thật (dự án JD Libary 5, lượt 14→16): lượt 14 bày ["Ngày gán JD", "Nhân viên được gán",
    // "Ngày hiệu lực", "Ngày hết hạn"] ở chế độ chọn nhiều; người dùng liệt kê ba cái đầu; lượt 16 hỏi lại
    // "có cần lưu thêm ngày hết hạn hay không?" — đốt trọn một lượt để nghe lại đúng một tiếng "không".
    [Fact]
    public void AChipLeftUnchecked_CountsAsAnswered()
    {
        var turns = new List<AgentConversation>
        {
            new()
            {
                Role = "assistant",
                Message = "Khi gán JD cho nhân viên, cần lưu những thông tin gì về lần gán đó?",
                Suggestions = JsonSerializer.Serialize(new[]
                    { "Ngày gán JD", "Nhân viên được gán", "Ngày hiệu lực", "Ngày hết hạn" }),
                SuggestionsMultiSelect = true
            },
            User("ngày gán JD, nhân viên được gán, ngày hiệu lực, mã JD")
        };

        var declined = AskedQuestionHistory.DeclinedChipKeys(turns);

        Assert.Contains(AskedQuestionHistory.Key("Ngày hết hạn"), declined);
        Assert.DoesNotContain(AskedQuestionHistory.Key("Ngày hiệu lực"), declined);

        Assert.True(AskedQuestionHistory.AsksAboutDeclinedChip(
            "Cảm ơn anh/chị! Vậy khi gán JD cho nhân viên, có cần lưu thêm ngày hết hạn hay không?", declined));

        // Ranh giới 1: câu ĐÀO SÂU về cùng chủ đề không phải hỏi lại — đó đúng là việc BA nên làm.
        Assert.False(AskedQuestionHistory.AsksAboutDeclinedChip(
            "Ngày hết hạn của một lần gán do ai đặt và dựa vào đâu?", declined));

        // Ranh giới 2: câu có/không về một chip người dùng ĐÃ chọn thì không nằm trong sổ này.
        Assert.False(AskedQuestionHistory.AsksAboutDeclinedChip(
            "Ngày hiệu lực có bắt buộc không?", declined));
    }

    // Chỉ lượt CHỌN NHIỀU mới sinh ra "chip bị bỏ": ở lượt chọn-một, các chip còn lại là phương án bị
    // loại theo luật của câu hỏi, không phải thứ người dùng đã cân nhắc rồi bỏ.
    [Fact]
    public void SinglePickChips_AreNotTreatedAsDeclined()
    {
        var turns = new List<AgentConversation>
        {
            new()
            {
                Role = "assistant",
                Message = "Khi JD đã available, ai là người gán JD cho nhân viên?",
                Suggestions = JsonSerializer.Serialize(new[] { "Manager tự gán", "HRBP gán", "Cả hai đều gán được" }),
                SuggestionsMultiSelect = false
            },
            User("Manager tự gán")
        };

        Assert.Empty(AskedQuestionHistory.DeclinedChipKeys(turns));
    }


    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // CÂU HỎI-VÉT ("…, còn có <cái gì> nào KHÁC không?")
    //
    // Ca thật (dự án quản lý khóa học bắt buộc — AI Call Log BAChat 2026-09-01). Người dùng vừa trả lời
    // xong câu vét về các trường hợp đặc biệt; hai lượt sau BA phát lại ĐÚNG khung câu ấy, chỉ thay vế
    // liệt kê bằng chính câu trả lời vừa nhận. Đo bằng phép thử tương đồng: bao phủ 0.75, Jaccard 0.52 —
    // dưới CẢ HAI ngưỡng, vì phần đổi chiếm gần nửa số từ. Hạ ngưỡng để bắt nó thì chặn oan hàng loạt câu
    // đào sâu thật, nên chỗ để bắt là HÌNH DẠNG câu.
    [Fact]
    public void IsSweepRepeat_CatchesTheSameSweepQuestionWithADifferentExampleList()
    {
        var tails = AskedQuestionHistory.SweepTailKeys(new[]
        {
            "Mình đã ghi nhận: tất cả nhân viên trong nhà máy sẽ dùng ứng dụng này. Vậy mình còn một điểm "
            + "cần làm rõ về các trường hợp đặc biệt: ngoài việc khóa học hết hạn, còn có trường hợp nào khác "
            + "cần xử lý không? Ví dụ như nhân viên nghỉ việc, chuyển phòng ban, hay khóa học bị hủy..."
        });

        Assert.True(AskedQuestionHistory.IsSweepRepeat(
            "Cảm ơn anh/chị. Mình đã ghi nhận: khi còn 1 tháng nữa là hết hạn hiệu lực thì gửi mail nhắc "
            + "nhở, và cứ cách 1 tuần gửi 1 lần cho đến khi nhân viên hoàn thành khóa học đó. Vậy mình còn một "
            + "điểm cần làm rõ về các trường hợp đặc biệt khác: ngoài việc nhân viên nghỉ việc và chuyển vai "
            + "trò, còn có trường hợp nào khác cần xử lý không? Ví dụ như khóa học bị hủy, hay nhân viên "
            + "chuyển phòng ban...", tails));

        // …và nó nằm ngoài tầm của phép thử tương đồng, nên hai phanh không thay thế nhau được.
        Assert.False(AskedQuestionHistory.IsRepeat(
            "Cảm ơn anh/chị. Mình đã ghi nhận: khi còn 1 tháng nữa là hết hạn hiệu lực thì gửi mail nhắc "
            + "nhở, và cứ cách 1 tuần gửi 1 lần cho đến khi nhân viên hoàn thành khóa học đó. Vậy mình còn một "
            + "điểm cần làm rõ về các trường hợp đặc biệt khác: ngoài việc nhân viên nghỉ việc và chuyển vai "
            + "trò, còn có trường hợp nào khác cần xử lý không? Ví dụ như khóa học bị hủy, hay nhân viên "
            + "chuyển phòng ban...",
            AskedQuestionHistory.Keys(new[] { "Mình đã ghi nhận: tất cả nhân viên trong nhà máy sẽ dùng ứng dụng này. Vậy mình còn một điểm "
            + "cần làm rõ về các trường hợp đặc biệt: ngoài việc khóa học hết hạn, còn có trường hợp nào khác "
            + "cần xử lý không? Ví dụ như nhân viên nghỉ việc, chuyển phòng ban, hay khóa học bị hủy..." })));
    }

    // Đuôi câu KHÁC nhau ⇒ không phải phát lại. Ca thật cùng buổi đó (lượt 26 → 28): BA hỏi cần báo cáo
    // gì, người dùng kể hai cái, BA hỏi tiếp "ngoài hai báo cáo này còn cần gì khác không" — đó là câu
    // vét TIẾP, không phải câu vét CŨ, và nó phải được đi qua.
    [Fact]
    public void IsSweepRepeat_DoesNotBlockTheNextSweepOnAnAnswerJustGiven()
    {
        var tails = AskedQuestionHistory.SweepTailKeys(new[]
        {
            "Vậy ngoài việc theo dõi hạn hiệu lực và gửi email nhắc nhở, anh/chị còn cần những báo cáo "
            + "hay thống kê nào từ ứng dụng này không?"
        });

        Assert.False(AskedQuestionHistory.IsSweepRepeat(
            "Vậy ngoài hai báo cáo này, anh/chị còn cần những báo cáo hay thống kê nào khác không?", tails));
    }

    [Fact]
    public void IsSweepRepeat_IgnoresQuestionsThatAreNotSweeps_OrWhoseTailIsTooShortToStandAlone()
    {
        // Không mang cụm vét ⇒ đứng ngoài sổ đuôi, dù có dấu phẩy và hỏi cùng chủ đề.
        Assert.Empty(AskedQuestionHistory.SweepTailKeys(new[]
        {
            "Khi nhân viên nghỉ việc, khóa học bắt buộc đã gán chuyển sang trạng thái nào?"
        }));

        // Có cụm vét nhưng đuôi cụt: "ai xử lý" là đuôi của vô số câu khác hẳn nhau.
        Assert.Empty(AskedQuestionHistory.SweepTailKeys(new[]
        {
            "Ngoài nghỉ việc, còn trường hợp nào khác, ai xử lý?"
        }));
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

    [Fact]
    public void IsSweepRepeat_DoesNotBlockTheSameShapeAskedAboutANewSubject()
    {
        var tails = AskedQuestionHistory.SweepTailKeys(new[] { AskedManagerRole });

        Assert.False(AskedQuestionHistory.IsSweepRepeat(
            "Mình ghi nhận: Quản lý trực tiếp xem danh sách nhân viên và khóa học bắt buộc của họ, xem lịch "
            + "sử học của họ. Vậy còn vai trò Nhân viên thì sao? Anh/chị cho mình biết: Nhân viên sẽ dùng ứng "
            + "dụng để làm những việc gì? Ví dụ: xem khóa học bắt buộc của mình, xem lịch sử học, hay còn "
            + "thao tác nào khác?", tails));
    }

    // …nhưng đổi CHỦ THỂ mới là thứ được đi qua: cùng vai, chỉ thay danh sách ví dụ, vẫn là hỏi lại.
    [Fact]
    public void IsSweepRepeat_StillCatchesTheSameSubjectWithADifferentExampleList()
    {
        var tails = AskedQuestionHistory.SweepTailKeys(new[] { AskedManagerRole });

        Assert.True(AskedQuestionHistory.IsSweepRepeat(
            "Mình ghi nhận thêm. Anh/chị cho mình biết: Quản lý trực tiếp sẽ dùng ứng dụng để làm những việc "
            + "gì? Ví dụ: duyệt kế hoạch đào tạo, xuất báo cáo, hay còn thao tác nào khác?", tails));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // LƯỢT PHÁT LẠI RỒI XIN GẬT
    //
    // Prompt bắt BA chốt lại điều đã ghi nhận rồi xin xác nhận, và câu xin gật ấy giống nhau ở mọi lượt.
    // Nó dài hơn ngưỡng "vế hỏi quá ngắn" nên ngưỡng đó không đỡ — lấy nó làm khoá là dựng sẵn một vụ
    // trùng khoá TUYỆT ĐỐI giữa hai lượt chốt hai điều khác hẳn nhau.
    [Fact]
    public void QuestionCore_KeepsTheWholeMessage_WhenTheQuestionClauseOnlyAsksForANod()
    {
        var keys = AskedQuestionHistory.Keys(new[]
        {
            "Vậy mình chốt lại những gì đã ghi nhận được cho đến giờ: ứng dụng quản lý việc học các khóa "
            + "bắt buộc; admin cấu hình vai trò và khóa học. Anh/chị thấy mình hiểu vậy đã đúng chưa?"
        });

        Assert.False(AskedQuestionHistory.IsRepeat(
            "Vậy mình chốt lại phần nhắc nhở: mail gửi trước hạn 1 tháng, sau đó mỗi tuần một lần cho đến "
            + "khi học xong. Anh/chị thấy mình hiểu vậy đã đúng chưa?", keys));
    }

    // Chiều ngược lại: một KỊCH BẢN MẪU hay VÍ DỤ TÍNH THỬ cũng kết bằng cụm xác nhận, nhưng nó CHỞ nội
    // dung — phát lại nguyên si vẫn phải bị bắt. Đây là ranh giới của điều khoản trên.
    [Fact]
    public void QuestionCore_StillComparesAWorkedExampleThatEndsWithTheSameConfirmationCue()
    {
        var example = "Ví dụ 23 nhân viên với sĩ số tối thiểu 8 và tối đa 12 thì hệ thống gợi ý mở 2 lớp — "
            + "đúng cách anh/chị tính không?";

        Assert.True(AskedQuestionHistory.IsRepeat(example, AskedQuestionHistory.Keys(new[] { example })));
    }

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
