using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Organization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ICOGenerator.Tests;

namespace ICOGenerator.Tests.Requirements;

// KHÔNG HỎI LẠI ĐIỀU NGƯỜI DÙNG VỪA TRẢ LỜI — ở mức lượt chat, không chỉ ở prompt.
//
// Bệnh gốc: bản đồ bao phủ là thứ DUY NHẤT dẫn dắt lượt hỏi, mà nó chỉ có độ phân giải theo NHÓM. Một
// dòng chưa đạt chuẩn [RÕ] (chuẩn cố ý khắt khe) đồng nghĩa "ưu tiên hỏi nhóm này", và vì mỗi câu hỏi
// của lượt gộp được gắn `group` = tên dòng bản đồ, model phát lại đúng câu hỏi mở đầu của nhóm đó —
// người dùng vừa trả lời xong đã bị hỏi lại nguyên văn, chip gợi ý chính là câu họ vừa gõ. Cùng triệu
// chứng khi lượt chắt lọc bản đồ hỏng (fail-open giữ bản cũ): cả cụm câu hỏi lượt trước được phát lại.
//
// Prompt đã cấm, nhưng prompt chỉ định hướng. Các bất biến dưới đây mới là cái phanh.
public class BAChatRepeatedQuestionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    private const string AskedRoles = "Ai sẽ sử dụng app này và vai trò của họ?";
    private const string AskedNotify = "Khi có nhân viên đạt 11 giờ, cách thức nhắc nhở và hành động tiếp theo ra sao?";

    // Câu MỞ xin một lời KỂ: theo luật "câu mở thì KHÔNG chip" nên lượt lưu nó không có gợi ý nào.
    private const string AskedStory =
        "Anh/chị kể giúp mình một lần gần nhất khi tạo và gán một JD cho nhân viên: bắt đầu từ đâu, "
        + "làm những bước nào, và ai tham gia vào quy trình đó?";

    // Bản đồ mà lượt chắt lọc (fake) trả về ở mọi test: hai nhóm người dùng VỪA trả lời vẫn bị giữ
    // [MỘT PHẦN] — đúng tình huống đã đẻ ra bệnh, vì đó là lúc prompt bảo BA "ưu tiên hỏi nhóm này".
    private static readonly string PartialMap =
        CoverageMapFixture.DistillReply("- ★ Mục tiêu / bài toán: [RÕ] hiển thị nhân viên làm quá 11 giờ.\n"
        + "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: quan hệ cấp trên của các vai trò\n"
        + "- Thông báo / nhắc nhở: [MỘT PHẦN] còn thiếu: khi nào thì gọi");

    public BAChatRepeatedQuestionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app giờ làm việc" });
        db.SaveChanges();
    }

    [Fact]
    public async Task RepeatedQuestionsAreDroppedFromTheBatch_OnlyTheNewOneSurvives()
    {
        await SeedAnsweredBatchAsync();

        // Lượt "xác nhận lại" kinh điển: hai câu cũ nguyên văn + một câu thật sự mới.
        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = "Cảm ơn bạn. Dưới đây là 3 câu xác nhận:",
                Questions = new List<BAChatQuestion>
                {
                    new() { Group = "Đối tượng người dùng & vai trò", Question = "Ai sẽ dùng app và vai trò của họ?", Suggestions = new List<string> { "Phòng bảo vệ", "Phòng nhân sự" } },
                    new() { Group = "Thông báo / nhắc nhở", Question = AskedNotify, Suggestions = new List<string> { "Gọi điện", "Gọi manager" } },
                    new() { Group = "Quy mô sử dụng", Question = "Áng chừng bao nhiêu nhân viên trong nhà máy?", Suggestions = new List<string> { "Dưới 500", "Trên 500" } }
                }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ xem dashboard, phòng nhân sự xem history");

        Assert.Equal(ChatWithBAResult.Ok, result.Status);
        // Còn đúng một câu MỚI ⇒ hạ về đường một-câu: câu hỏi lên thẳng nội dung lượt, gợi ý riêng của nó
        // được nâng lên làm chip. Người dùng không thấy bóng dáng nào của hai câu cũ.
        Assert.Empty(result.Questions);
        Assert.Equal("Áng chừng bao nhiêu nhân viên trong nhà máy?", result.Reply);
        Assert.Equal(new[] { "Dưới 500", "Trên 500" }, result.Suggestions);
        Assert.DoesNotContain("vai trò của họ", result.Reply);

        var saved = await LastAssistantTurnAsync();
        Assert.Equal("Áng chừng bao nhiêu nhân viên trong nhà máy?", saved.Message);
        Assert.Empty(ConversationTurnRenderer.ParseQuestions(saved.Questions));
    }

    [Fact]
    public async Task WhenEveryQuestionIsARepeat_TheTurnBecomesTheDeterministicFollowUp()
    {
        await SeedAnsweredBatchAsync();

        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = "Cảm ơn bạn. Mình xác nhận lại mấy điểm sau:",
                Questions = new List<BAChatQuestion>
                {
                    new() { Group = "Đối tượng người dùng & vai trò", Question = AskedRoles, Suggestions = new List<string> { "Phòng bảo vệ" } },
                    new() { Group = "Thông báo / nhắc nhở", Question = AskedNotify, Suggestions = new List<string> { "Gọi điện" } }
                }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ xem dashboard, phòng nhân sự xem history");

        // Không im lặng và không để lại câu dẫn cụt ("Mình xác nhận lại mấy điểm sau:" mà chẳng có điểm
        // nào): lượt được thay bằng bước kế tiếp suy TẤT ĐỊNH từ bản đồ — hỏi ĐÚNG phần còn thiếu của
        // dòng ★ cốt lõi đang treo, và chỉ MỘT chỗ (chỗ còn lại để dành lượt sau, không hỏi dồn).
        Assert.Empty(result.Questions);
        Assert.DoesNotContain("xác nhận lại mấy điểm sau", result.Reply);
        Assert.Contains("quan hệ cấp trên của các vai trò", result.Reply);
        Assert.DoesNotContain("khi nào thì gọi", result.Reply);

        // …và lượt đó KHÔNG đọc sổ sách của hệ thống ra màn hình: không nhãn nhóm, không đếm nhóm còn lại.
        Assert.DoesNotContain("nhóm", result.Reply, StringComparison.OrdinalIgnoreCase);

        var saved = await LastAssistantTurnAsync();
        Assert.Equal(result.Reply, saved.Message);
    }

    [Fact]
    public async Task ASingleQuestionTurnThatRepeatsIsReplacedToo()
    {
        await SeedAnsweredBatchAsync();

        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = AskedRoles,
                Suggestions = new List<string> { "Phòng bảo vệ", "Phòng nhân sự" }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ xem dashboard, phòng nhân sự xem history");

        Assert.NotEqual(AskedRoles, result.Reply);
        Assert.Contains("quan hệ cấp trên của các vai trò", result.Reply);
        Assert.DoesNotContain("Đối tượng người dùng & vai trò", result.Reply);
    }

    // CÂU MỞ (xin một lời KỂ) là loại câu đắt nhất của buổi phỏng vấn và cũng là loại KHÔNG có chip —
    // trước đây đúng vì thế mà nó không bao giờ vào sổ "đã hỏi", nên phanh không có gì để so và BA phát
    // lại được nguyên văn. Ca thật (dự án JD Libary, lượt 2 và lượt 4): cùng một câu xin kể quy trình
    // tạo/gán JD hỏi hai lượt liền, người dùng đáp "mình nói ở trên rồi đó".
    [Fact]
    public async Task AnOpenStorytellingQuestionIsRememberedToo_AndNeverAskedTwice()
    {
        await SeedAskedOpenQuestionAsync();

        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply { Message = AskedStory, OpenEnded = true }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "mình nói ở trên rồi đó");

        Assert.NotEqual(AskedStory, result.Reply);
        Assert.DoesNotContain("một lần gần nhất", result.Reply);
        // Thay bằng bước kế tiếp suy TẤT ĐỊNH từ bản đồ — không im lặng, không câu dẫn cụt.
        Assert.Contains("quan hệ cấp trên của các vai trò", result.Reply);
    }

    // Ca thật (dự án JD Libary 4, lượt 16 → 20), và là chỗ phanh này câm ở đúng lúc prompt làm ĐÚNG nhất:
    // "QUY TẮC PHÁT LẠI" bắt BA chép lại điều đã ghi nhận trước khi hỏi, nên mỗi lượt hỏi một câu mang theo
    // một đoạn phát lại KHÁC nhau (nó chép lời người dùng vừa nói). So cả `Message` thì hai lượt hỏi CÙNG
    // một câu vẫn lệch nhau vì hai đoạn phát lại lệch nhau — lượt 20 lọt qua, hỏi lại nguyên câu của lượt
    // 16 kèm một chip chép lại đúng câu trả lời người dùng vừa gõ ở lượt 19.
    [Fact]
    public async Task ARepeatHiddenBehindItsRecapPreamble_IsStillCaught()
    {
        await SeedAskedWithRecapAsync();

        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = "Cảm ơn anh/chị! Mình đã ghi nhận: JD đã được HoD approve thì không sửa trực tiếp "
                    + "được, muốn chỉnh sửa thì phải upgrade version (giữ nguyên mã JD cũ, tăng version 1, 2, "
                    + "3...) và version mới phải trải qua quy trình approve lại từ đầu. Mình còn một điểm cần "
                    + "làm rõ: khi JD đã available và được gán cho nhân viên, nếu cần chỉnh sửa thì xử lý thế nào?",
                Suggestions = new List<string> { "Upgrade version và duyệt lại từ đầu", "Sửa bất kỳ lúc nào" }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Upgrade version và duyệt lại từ đầu");

        Assert.DoesNotContain("nếu cần chỉnh sửa thì xử lý thế nào", result.Reply, StringComparison.Ordinal);
        // Thay bằng bước kế tiếp suy TẤT ĐỊNH từ bản đồ — không im lặng, không câu dẫn cụt.
        Assert.Contains("quan hệ cấp trên của các vai trò", result.Reply, StringComparison.Ordinal);
    }

    // CA THẬT (dự án quản lý khóa học bắt buộc — AI Call Log BAChat 2026-09-01, lượt 38 và lượt cuối).
    // Hai lỗ hổng chồng lên nhau, và đây là chỗ chúng gặp nhau:
    //   1. Câu hỏi KHÔNG chip và không mang cụm xin-kể ⇒ cờ "câu mở" không bật ⇒ phanh không chạy lần
    //      nào, dù câu đó vẫn được ghi vào sổ "đã hỏi". Gần một nửa số lượt của buổi đó có hình dạng này.
    //   2. Kể cả khi chạy, phép thử tương đồng cũng hụt: model giữ nguyên khung câu VÉT và chỉ thay vế
    //      liệt kê bằng chính câu trả lời vừa nhận (bao phủ 0.75 / Jaccard 0.52 — dưới cả hai ngưỡng).
    [Fact]
    public async Task AChiplessSweepQuestion_IsCaught_EvenWhenTheExampleListChanged()
    {
        await SeedAskedSweepQuestionAsync();

        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = "Cảm ơn anh/chị. Mình đã ghi nhận: khi còn 1 tháng nữa là hết hạn hiệu lực thì gửi "
                    + "mail nhắc nhở, và cứ cách 1 tuần gửi 1 lần cho đến khi nhân viên hoàn thành khóa học đó. "
                    + "Vậy mình còn một điểm cần làm rõ về các trường hợp đặc biệt khác: ngoài việc nhân viên "
                    + "nghỉ việc và chuyển vai trò, còn có trường hợp nào khác cần xử lý không? Ví dụ như khóa "
                    + "học bị hủy, hay nhân viên chuyển phòng ban..."
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(
            _projectId, "khi thời hạn hiệu lực còn 1 tháng thì gửi mail nhắc nhở, và cứ cách 1 tuần gửi 1 lần");

        Assert.DoesNotContain("còn có trường hợp nào khác cần xử lý không", result.Reply, StringComparison.Ordinal);
        // Thay bằng bước kế tiếp suy TẤT ĐỊNH từ bản đồ — không im lặng, không câu dẫn cụt.
        Assert.Contains("quan hệ cấp trên của các vai trò", result.Reply, StringComparison.Ordinal);
    }

    // MẶT KIA của cùng cái phanh (ca thật cùng dự án, 2026-09-03). Khuôn "<ai đó> sẽ dùng ứng dụng để làm
    // những việc gì? Ví dụ: A, B, hay còn thao tác nào khác?" để CHỦ THỂ ở câu trước và cái đuôi là văn mẫu
    // dùng lại cho mọi vai. BA hỏi xong vai Quản lý trực tiếp rồi hỏi sang vai Nhân viên — một câu hoàn
    // toàn mới — và bị chặn vì trùng đuôi; lượt đó bị thay bằng câu chặn của cổng (xin một ví dụ tính thử)
    // và vai Nhân viên không được hỏi lượt nào. Câu hỏi MỚI của BA phải tới được người dùng.
    [Fact]
    public async Task ANewSubjectAskedWithTheSameSweepShape_ReachesTheUser()
    {
        await SeedAskedManagerRoleQuestionAsync();

        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = "Mình ghi nhận: Quản lý trực tiếp xem danh sách nhân viên và khóa học bắt buộc của "
                    + "họ, xem lịch sử học của họ. Vậy còn vai trò Nhân viên thì sao? Anh/chị cho mình biết: "
                    + "Nhân viên sẽ dùng ứng dụng để làm những việc gì? Ví dụ: xem khóa học bắt buộc của mình, "
                    + "xem lịch sử học, hay còn thao tác nào khác?"
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(
            _projectId, "quản lý sẽ xem danh sách nhân viên và khóa học bắt buộc của họ, xem lịch sử học của họ");

        Assert.Contains("Nhân viên sẽ dùng ứng dụng để làm những việc gì", result.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("quan hệ cấp trên của các vai trò", result.Reply, StringComparison.Ordinal);
    }

    // …và câu MỞ cũng vậy. Lượt hỏi một câu không có mảng `questions`, câu hỏi nằm thẳng ở `message` —
    // đường thứ hai mà transcript phải chở được thì việc bỏ khối prompt mới an toàn.
    [Fact]
    public async Task AnOpenQuestionAskedBefore_ReachesTheModelThroughTheTranscript()
    {
        await SeedAskedOpenQuestionAsync();

        var llm = new FakeLlm(PartialMap) { ChatReply = new BAChatReply { Message = "Nhà máy có bao nhiêu nhân viên?", Suggestions = new List<string> { "Dưới 500" } } };

        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "khoảng 500");

        Assert.Contains(AskedStory, string.Join("\n", llm.LastChatAssistantMessages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGroupTheUserReopenedMayBeAskedAgain()
    {
        await SeedAnsweredBatchAsync();

        // Người dùng vừa nói trong chat "nhóm vai trò chưa đúng" và lượt chắt lọc đã đánh dấu dòng đó ⇒ họ
        // CHỦ ĐỘNG xin được hỏi lại. Phanh phải nhường, nếu không lời đính chính của họ rơi vào im lặng:
        // bản đồ đã hạ nhóm xuống nhưng câu hỏi của BA lại bị lọc mất vì trùng câu cũ.
        var reopenedMap = CoverageMapFixture.DistillReply(
            "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: " + AskedQuestionHistory.ReopenNote + "\n"
            + "- Thông báo / nhắc nhở: [MỘT PHẦN] còn thiếu: khi nào thì gọi");

        var llm = new FakeLlm(reopenedMap)
        {
            ChatReply = new BAChatReply
            {
                Message = "Mình hỏi lại mấy điểm sau nhé:",
                Questions = new List<BAChatQuestion>
                {
                    new() { Group = "Đối tượng người dùng & vai trò", Question = AskedRoles, Suggestions = new List<string> { "Phòng bảo vệ" } },
                    new() { Group = "Thông báo / nhắc nhở", Question = AskedNotify, Suggestions = new List<string> { "Gọi điện" } }
                }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Nhóm vai trò chưa đúng");

        // Câu của nhóm ĐƯỢC MỞ LẠI sống sót; câu của nhóm còn lại vẫn bị loại vì đã hỏi rồi ⇒ còn một câu
        // ⇒ hạ về đường một-câu.
        Assert.Empty(result.Questions);
        Assert.Equal(AskedRoles, result.Reply);
    }

    [Fact]
    public async Task ANewQuestionOnAPartialGroupIsNotBlocked()
    {
        await SeedAnsweredBatchAsync();

        // Đúng việc BA phải làm với nhóm [MỘT PHẦN]: hỏi phần "còn thiếu:", KHÔNG phát lại câu mở đầu.
        const string followUp = "Anh/chị vừa nói phòng bảo vệ gọi điện nhắc — cuộc gọi đó nổ ra ngay lúc chạm 11 giờ hay tới ca trực mới rà một lượt?";
        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply { Message = followUp, Suggestions = new List<string> { "Ngay lúc chạm 11 giờ", "Theo ca trực" } }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ gọi điện nhắc");

        Assert.Equal(followUp, result.Reply);
    }

    // NỬA CÒN LẠI CỦA PHANH: model phải ĐỌC được các câu đã hỏi, không chỉ bị lọc sau khi lỡ hỏi lại.
    //
    // Trước đây việc đó do một khối system message riêng ("## Các câu hỏi BẠN ĐÃ HỎI ở những lượt trước")
    // đảm nhiệm. Khối ấy đã bỏ vì nó dựng từ ĐÚNG danh sách lượt mà transcript gửi nguyên văn ngay sau
    // đó — một bản chép đôi tốn ~5.000 ký tự mỗi lượt, nằm ngoài prefix cache. Hai test dưới đây khoá
    // lại đúng tiền đề của việc bỏ ấy: câu cũ vẫn tới model, qua transcript, và ĐỌC ĐƯỢC.
    [Fact]
    public async Task PreviouslyAskedQuestionsReachTheModelThroughTheTranscript()
    {
        await SeedAnsweredBatchAsync();

        var llm = new FakeLlm(PartialMap) { ChatReply = new BAChatReply { Message = "Nhà máy có bao nhiêu nhân viên?", Suggestions = new List<string> { "Dưới 500" } } };

        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ xem dashboard");

        // Lượt GỘP: câu hỏi nằm ở mảng `questions` của lượt BA cũ, không ở `message`.
        var transcript = string.Join("\n", llm.LastChatAssistantMessages);
        Assert.Contains(AskedRoles, transcript, StringComparison.Ordinal);
        Assert.Contains(AskedNotify, transcript, StringComparison.Ordinal);

        // Và KHÔNG còn bản chép thứ hai trong system message — đó là toàn bộ điểm của việc bỏ khối.
        Assert.DoesNotContain(llm.LastChatSystemMessages, m => m.Contains(AskedRoles, StringComparison.Ordinal));
    }

    // Chữ có dấu phải đi lên model NGUYÊN DẠNG. Encoder mặc định của JsonSerializer escape mọi ký tự
    // non-ASCII, nên lượt BA cũ sẽ thành "C\u1EA3m \u01A1n anh/ch\u1ECB…": tốn gấp mấy lần token cho
    // cùng một nội dung, và biến transcript — nay là chỗ DUY NHẤT chở các câu đã hỏi — thành thứ khó
    // đọc đúng lúc phanh chống hỏi lại phụ thuộc vào nó. Hai thay đổi ấy là một cặp, nên khoá lại đây.
    [Fact]
    public async Task TheTranscriptKeepsVietnameseCharactersUnescaped()
    {
        await SeedAnsweredBatchAsync();

        var llm = new FakeLlm(PartialMap) { ChatReply = new BAChatReply { Message = "Nhà máy có bao nhiêu nhân viên?", Suggestions = new List<string> { "Dưới 500" } } };

        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ xem dashboard");

        var transcript = string.Join("\n", llm.LastChatAssistantMessages);
        Assert.DoesNotContain("\\u", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheCoverageDistillFails_TheTurnReportsTheMapIsStale()
    {
        await SeedAnsweredBatchAsync();

        // Bản đồ đứng im là chuyện người dùng phải thấy: BA vừa dẫn lượt bằng bản đồ CŨ, nên nó có thể
        // hỏi lại đúng nhóm vừa được trả lời — triệu chứng trông hệt "BA không nghe mình nói".
        var llm = new FakeLlm(coverageMap: null) { ChatReply = new BAChatReply { Message = "Nhà máy có bao nhiêu nhân viên?", Suggestions = new List<string> { "Dưới 500" } } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ xem dashboard");

        Assert.True(result.CoverageStale);
        Assert.Equal(2, llm.CoverageCalls); // đã thử lại một lần trước khi chịu thua
    }

    // Hội thoại nền của mọi test: BA hỏi GỘP hai câu, người dùng trả lời cả hai trong một lượt (đúng
    // định dạng "- câu hỏi: trả lời" mà thẻ hỏi gộp sinh ra).
    private async Task SeedAnsweredBatchAsync()
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "user",
            Message = "Cần app hiển thị nhân viên làm quá 11 giờ trong nhà máy",
            CreatedAt = baseTime
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "assistant",
            Message = "Cảm ơn. Mình hỏi 2 điểm sau nhé:",
            Questions = JsonSerializer.Serialize(new[]
            {
                new BAChatQuestion { Group = "Đối tượng người dùng & vai trò", Question = AskedRoles, Suggestions = new List<string> { "Phòng bảo vệ" } },
                new BAChatQuestion { Group = "Thông báo / nhắc nhở", Question = AskedNotify, Suggestions = new List<string> { "Gọi điện" } }
            }),
            CreatedAt = baseTime.AddSeconds(1)
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "user",
            Message = $"- {AskedRoles}: Phòng bảo vệ xem dashboard, phòng nhân sự xem history\n"
                      + $"- {AskedNotify}: Gọi điện cho nhân viên, không được thì gọi manager",
            CreatedAt = baseTime.AddSeconds(2)
        });
        await db.SaveChangesAsync();
    }

    // Hội thoại nền cho ca ĐỔI CHỦ THỂ: BA vừa hỏi xong vai Quản lý trực tiếp bằng khuôn câu có đuôi vét,
    // và người dùng đã trả lời.
    private async Task SeedAskedManagerRoleQuestionAsync()
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "assistant",
            Message = "Cảm ơn anh/chị. Mình ghi nhận: Admin được người quản trị hệ thống chỉ định sẵn. Bây giờ "
                + "mình muốn làm rõ thêm về vai trò của Quản lý trực tiếp trong ứng dụng. Anh/chị cho mình "
                + "biết: Quản lý trực tiếp sẽ dùng ứng dụng để làm những việc gì? Ví dụ: xem danh sách nhân "
                + "viên và khóa học bắt buộc của họ, hay còn thao tác nào khác?",
            CreatedAt = baseTime
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "user",
            Message = "quản lý sẽ xem danh sách nhân viên và khóa học bắt buộc của họ, xem lịch sử học của họ",
            CreatedAt = baseTime.AddSeconds(1)
        });
        await db.SaveChangesAsync();
    }

    // Hội thoại nền cho ca câu HỎI-VÉT: BA hỏi vét các trường hợp đặc biệt — KHÔNG chip, đúng như lượt
    // thật — và người dùng chỉ trả lời được hai trong ba ca mà chính BA nêu ví dụ.
    private async Task SeedAskedSweepQuestionAsync()
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "assistant",
            Message = "Mình đã ghi nhận: tất cả nhân viên trong nhà máy sẽ dùng ứng dụng này. Vậy mình còn một "
                + "điểm cần làm rõ về các trường hợp đặc biệt: ngoài việc khóa học hết hạn, còn có trường hợp "
                + "nào khác cần xử lý không? Ví dụ như nhân viên nghỉ việc, chuyển phòng ban, hay khóa học bị hủy...",
            CreatedAt = baseTime
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "user",
            Message = "nhân viên nghỉ việc thì khóa học bắt buộc chuyển thành \"Đóng\", nhân viên chuyển vai "
                + "trò thì gán khóa mới theo vai trò mới",
            CreatedAt = baseTime.AddSeconds(1)
        });
        await db.SaveChangesAsync();
    }

    // Hội thoại nền cho hai test câu MỞ: BA xin một lời kể (không chip), người dùng kể xong.
    // Lượt BA đúng khuôn prompt bắt: một đoạn PHÁT LẠI rồi mới tới câu hỏi.
    private async Task SeedAskedWithRecapAsync()
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "assistant",
            Message = "Cảm ơn anh/chị! Mình đã ghi nhận: Manager tự quản lý JD của orgUnit mình. Vậy khi JD đã "
                + "available và được gán cho nhân viên, nếu cần chỉnh sửa hoặc ngừng sử dụng JD đó thì xử lý "
                + "như thế nào?",
            Suggestions = "[\"Chỉ được sửa khi chưa gán cho ai\"]",
            CreatedAt = baseTime
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "user",
            Message = "Ngừng sử dụng thì không gán mới nữa nhưng vẫn giữ lịch sử",
            CreatedAt = baseTime.AddSeconds(1)
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAskedOpenQuestionAsync()
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "assistant",
            Message = AskedStory,
            CreatedAt = baseTime
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "user",
            Message = "hiện tại việc tạo và gán JD được HRBP làm trong 2 file excel, HRBP tự thêm sửa xóa",
            CreatedAt = baseTime.AddSeconds(1)
        });
        await db.SaveChangesAsync();
    }

    private async Task<AgentConversation> LastAssistantTurnAsync()
    {
        await using var db = NewDb();
        return await db.AgentConversations
            .Where(c => c.ProjectId == _projectId && c.Role == "assistant")
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .FirstAsync();
    }

    // Cùng harness dựng BAChatService như BAChatRetryTests (không scope factory ⇒ các bước chuẩn bị chạy
    // tuần tự trên chính db của test).
    private static BAChatService NewSut(AppDbContext db, ILlmClient llm)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new StubPrompts();
        return new BAChatService(
            db,
            llm,
            prompts,
            new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance),
            new BAChatReplyParser(),
            new ConversationMemoryService(db, llm, prompts),
            new UserMemoryService(db, llm, prompts),
            new RequirementCoverageService(db, llm, prompts, new CoverageChecklist(prompts)),
            new OrganizationContextService(db, prompts,
                new OrgChartProvider(db, new MemoryCache(new MemoryCacheOptions())),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new InterviewScopeService(db, llm, prompts),
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // coverageMap = null ⇒ lượt chắt lọc bản đồ LỖI (fail-open). Mọi lời gọi text khác (bộ nhớ, hồ sơ
    // user, decision log) cũng fail-open nên không cần dựng riêng.
    private sealed class FakeLlm : ILlmClient
    {
        private readonly string? _coverageMap;

        public FakeLlm(string? coverageMap) => _coverageMap = coverageMap;

        public BAChatReply ChatReply = new() { Message = "Đã ghi nhận." };
        public int CoverageCalls;
        public List<string> LastChatSystemMessages = new();
        public List<string> LastChatAssistantMessages = new();

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            if (logContext.Purpose != "BARequirementCoverage")
                return Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

            CoverageCalls++;
            return Task.FromResult(_coverageMap == null
                ? new LlmCallResult { IsSuccess = false, ErrorMessage = "distill lỗi" }
                : new LlmCallResult { IsSuccess = true, Content = _coverageMap });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {

            // Bản đồ bao phủ nay đi qua đường structured output (RequirementCoverageService). Fake trả về
            // Value null để service rơi xuống nhánh parse văn xuôi — đúng nhánh mà một model không nhận
            // response_format sẽ chạy — nên các test ở đây vẫn seed bản đồ bằng text như trước.
            if (logContext.Purpose == "BARequirementCoverage")
                return Task.FromResult((ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).Result, (T?)null));
            if (logContext.Purpose != "BAChat")
                throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}");

            LastChatSystemMessages = messages
                .Where(m => m.Role == ChatRole.System)
                .Select(m => m.Text ?? string.Empty)
                .ToList();

            // Các lượt BA cũ trong transcript. Từ khi khối "## Các câu hỏi BẠN ĐÃ HỎI…" bị bỏ, ĐÂY là
            // chỗ duy nhất model đọc lại được các câu nó đã hỏi, nên test phải soi đúng chỗ này.
            LastChatAssistantMessages = messages
                .Where(m => m.Role == ChatRole.Assistant)
                .Select(m => m.Text ?? string.Empty)
                .ToList();

            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)(object)ChatReply));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
