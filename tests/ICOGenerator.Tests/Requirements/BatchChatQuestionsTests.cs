using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// LƯỢT HỎI GỘP: BA được phép đặt 2–4 câu hỏi ĐỘC LẬP trong cùng một lượt, thay cho cổng "chốt nhanh" đã
// bỏ. Cổng cũ rút ngắn phỏng vấn bằng cách để BA tự soạn phương án cho mọi nhóm còn trống rồi ghi vào
// hội thoại NHƯ LỜI NGƯỜI DÙNG — bản đồ bao phủ đầy lên mà không ai thật sự trả lời câu nào. Bốn bất
// biến dưới đây là thứ giữ cho đường mới không trượt về đúng chỗ đó:
//
//  1. TRẦN CỨNG 4 câu/lượt, áp ở CẢ hai đường vào (parse text và structured output). Prompt định hướng,
//     nhưng model luôn có xu hướng gộp tối đa để "xong sớm"; một lượt 12 câu là cổng chốt nhanh đội lốt
//     phỏng vấn. Trần này là cái phanh duy nhất mang tính tất định.
//  2. GỘP một-câu thì HẠ về đường một-câu — không bao giờ dựng thẻ nhiều dòng chỉ có một dòng, và câu
//     hỏi phải được nối vào message (message của lượt gộp chỉ là câu dẫn, mất nó là mất câu hỏi).
//  3. Hai đường trả lời LOẠI TRỪ nhau: có thẻ hỏi thì không có chip lượt-đơn. Chip bấm là GỬI NGAY, để
//     cả hai cùng sống thì nó cướp lượt trước khi người dùng kịp trả lời các câu còn lại.
//  4. Các câu hỏi phải đi vào TRANSCRIPT. Message của lượt gộp là câu dẫn, nên không render cột Questions
//     thì mọi tầng chắt lọc (bản đồ bao phủ, Product Brief, decision log) chỉ thấy câu trả lời mà không
//     biết nó trả lời cho câu hỏi nào.
public class BatchChatQuestionsTests
{
    private readonly BAChatReplyParser _parser = new();

    private static string Json(params (string group, string question, string[] suggestions)[] questions) =>
        JsonSerializer.Serialize(new
        {
            message = "Mình hỏi nhanh mấy điểm sau nhé:",
            suggestions = Array.Empty<string>(),
            questions = questions.Select(q => new { group = q.group, question = q.question, suggestions = q.suggestions })
        });

    [Fact]
    public void Parse_BatchTurn_KeepsEveryQuestionWithItsOwnSuggestions()
    {
        var reply = _parser.Parse(Json(
            ("Thông báo / nhắc nhở", "Khi đơn được duyệt, ai cần được báo?", new[] { "Chỉ người gửi", "Người gửi và quản lý" }),
            ("Quy mô sử dụng", "Áng chừng bao nhiêu người sẽ dùng?", new[] { "Dưới 20 người", "20–100 người" })));

        Assert.Equal(2, reply.Questions.Count);
        Assert.Equal("Thông báo / nhắc nhở", reply.Questions[0].Group);
        Assert.Equal("Khi đơn được duyệt, ai cần được báo?", reply.Questions[0].Question);
        Assert.Equal(new[] { "Dưới 20 người", "20–100 người" }, reply.Questions[1].Suggestions);
    }

    [Fact]
    public void Parse_MoreThanFourQuestions_IsTruncatedToTheHardCap()
    {
        var many = Enumerable.Range(1, 12)
            .Select(i => ($"Nhóm {i}", $"Câu hỏi số {i}?", new[] { "A", "B" }))
            .ToArray();

        var reply = _parser.Parse(Json(many));

        Assert.Equal(4, reply.Questions.Count);
        Assert.Equal("Câu hỏi số 4?", reply.Questions[3].Question);
    }

    // Đường structured output trả thẳng BAChatReply, KHÔNG đi qua Parse. Nếu trần chỉ sống trong Parse
    // thì các model tốt (đường mặc định khi bật structured output) lại là các model không bị chặn.
    [Fact]
    public void Normalize_StructuredOutputPath_AppliesTheSameHardCap()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mấy điểm sau nhé:",
            Questions = Enumerable.Range(1, 9)
                .Select(i => new BAChatQuestion { Question = $"Câu {i}?", Suggestions = new List<string> { "A", "B" } })
                .ToList()
        });

        Assert.Equal(4, reply.Questions.Count);
    }

    [Fact]
    public void Normalize_DuplicateAndEmptyQuestions_AreDropped()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mấy điểm sau nhé:",
            Questions = new List<BAChatQuestion>
            {
                new() { Question = "Ai duyệt đơn?", Suggestions = new List<string> { "Quản lý trực tiếp" } },
                new() { Question = "   ", Suggestions = new List<string> { "A" } },
                new() { Question = "Ai duyệt đơn?", Suggestions = new List<string> { "Trưởng phòng" } },
                new() { Question = "Bao nhiêu người dùng?", Suggestions = new List<string> { "Dưới 20" } }
            }
        });

        Assert.Equal(new[] { "Ai duyệt đơn?", "Bao nhiêu người dùng?" },
            reply.Questions.Select(q => q.Question));
    }

    [Fact]
    public void Parse_SingleQuestionInBatchShape_CollapsesToTheSingleQuestionPath()
    {
        var reply = _parser.Parse(Json(
            ("Quy mô sử dụng", "Áng chừng bao nhiêu người sẽ dùng?", new[] { "Dưới 20 người", "Trên 100 người" })));

        Assert.Empty(reply.Questions);
        // Câu hỏi được nối vào message: bỏ nó đi thì màn hình chỉ còn câu dẫn "Mình hỏi nhanh mấy điểm sau nhé:".
        Assert.Contains("Áng chừng bao nhiêu người sẽ dùng?", reply.Message);
        Assert.Equal(new[] { "Dưới 20 người", "Trên 100 người" }, reply.Suggestions);
    }

    [Fact]
    public void Normalize_CollapsedSingleQuestion_IsNotDuplicatedWhenMessageAlreadyAsksIt()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Áng chừng bao nhiêu người sẽ dùng?",
            Questions = new List<BAChatQuestion>
            {
                new() { Question = "Áng chừng bao nhiêu người sẽ dùng?", Suggestions = new List<string> { "Dưới 20 người" } }
            }
        });

        Assert.Equal("Áng chừng bao nhiêu người sẽ dùng?", reply.Message);
    }

    // Bất biến 3: chip lượt-đơn bấm là GỬI NGAY. Một lượt vừa có thẻ hỏi vừa có chip nghĩa là người dùng
    // bấm một chip cho câu thứ nhất là lượt bay đi, ba câu còn lại rơi vào khoảng không.
    [Fact]
    public void Normalize_BatchTurn_DropsTurnLevelSuggestions()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mấy điểm sau nhé:",
            Suggestions = new List<string> { "Đồng ý", "Tôi muốn khác" },
            MultiSelect = true,
            Questions = new List<BAChatQuestion>
            {
                new() { Question = "Ai duyệt đơn?", Suggestions = new List<string> { "Quản lý trực tiếp" } },
                new() { Question = "Bao nhiêu người dùng?", Suggestions = new List<string> { "Dưới 20" } }
            }
        });

        Assert.Empty(reply.Suggestions);
        Assert.False(reply.MultiSelect);
        Assert.Equal(2, reply.Questions.Count);
    }

    [Fact]
    public void Normalize_MultiSelectWithoutSuggestions_IsTurnedOff()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Gồm những vai trò nào?",
            MultiSelect = true
        });

        Assert.False(reply.MultiSelect);
    }

    [Fact]
    public void Render_AssistantBatchTurn_AppendsEveryQuestionToTheTranscript()
    {
        var questions = JsonSerializer.Serialize(new List<BAChatQuestion>
        {
            new()
            {
                Group = "Thông báo / nhắc nhở",
                Question = "Khi đơn được duyệt, ai cần được báo?",
                Suggestions = new List<string> { "Chỉ người gửi", "Người gửi và quản lý" }
            },
            new() { Group = "", Question = "Bao nhiêu người sẽ dùng?", Suggestions = new List<string>() }
        });

        var rendered = ConversationTurnRenderer.Render(new AgentConversation
        {
            Role = "assistant",
            Message = "Mình hỏi nhanh mấy điểm sau nhé:",
            Questions = questions
        }).Replace("\r\n", "\n");

        Assert.Equal(
            "BA: Mình hỏi nhanh mấy điểm sau nhé:\n" +
            "   (Các câu hỏi đã đặt trong lượt này: " +
            "[1] Thông báo / nhắc nhở — Khi đơn được duyệt, ai cần được báo? (gợi ý: Chỉ người gửi / Người gửi và quản lý); " +
            "[2] Bao nhiêu người sẽ dùng?)",
            rendered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ khong-phai-json-hop-le")]
    public void ParseQuestions_MissingOrBrokenColumn_IsTreatedAsAnOrdinarySingleQuestionTurn(string? questions)
    {
        Assert.Empty(ConversationTurnRenderer.ParseQuestions(questions));
        Assert.Equal("BA: Ai duyệt đơn?", ConversationTurnRenderer.Render(new AgentConversation
        {
            Role = "assistant",
            Message = "Ai duyệt đơn?",
            Questions = questions
        }));
    }
}
