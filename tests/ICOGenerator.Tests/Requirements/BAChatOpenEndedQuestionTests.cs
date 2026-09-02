using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// CÂU HỎI MỞ thì KHÔNG có chip.
//
// Luật cũ bắt "mọi câu hỏi đều phải kèm gợi ý", nên BA hỏi xin một câu chuyện rồi vẫn dựng ra một hàng
// chip. Lỗi thật đã gặp trên màn hình:
//
//   "Anh/chị kể giúp một lần gần nhất lập kế hoạch cho các lớp học trong năm: bắt đầu từ đâu, thực hiện
//    những bước nào, và kết quả cuối cùng cần có là gì?"
//   ["Đã có danh sách khóa học", "Bắt đầu từ nhu cầu đào tạo", "Đang theo dõi bằng Excel", …]
//
// Bốn chip chỉ chạm vế "bắt đầu từ đâu". Ở lượt hỏi MỘT câu, bấm chip là GỬI NGAY — nên "các bước" và
// "kết quả cuối cùng", đúng hai thứ đắt nhất, không bao giờ được kể; rồi mẩu bốn chữ đó được chắt vào bản
// đồ bao phủ như câu trả lời thật của người dùng, và nhóm coi như đã hỏi xong.
//
// Ba bất biến giữ chỗ này:
//  1. `openEnded` ⇒ chip bị XÓA. Cờ và bộ chip không bao giờ cùng sống — UI chỉ có một chỗ trả lời.
//  2. Sửa CHỈ MỘT CHIỀU (đóng → mở): parser tự bật `openEnded` cho câu xin-lời-kể nhận diện được, nhưng
//     không bao giờ tự tắt cờ BA đã đặt. Bật nhầm thì người dùng phải gõ thay vì bấm; bỏ sót thì sinh ra
//     một câu trả lời cụt mà mọi tầng sau tin là lời người dùng. Không cùng hạng giá.
//  3. Áp ở CẢ hai đường vào (Parse cho model trả text, Normalize cho structured output) và cho CẢ câu
//     lượt-đơn lẫn từng câu trong lượt gộp.
public class BAChatOpenEndedQuestionTests
{
    private readonly BAChatReplyParser _parser = new();

    // Chính ca đã gặp trên màn hình: BA xin lời kể nhưng vẫn kèm chip trả lời được một mẩu.
    [Fact]
    public void Normalize_StoryQuestionWithChips_DropsTheChips()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Anh/chị kể giúp một lần gần nhất lập kế hoạch cho các lớp học trong năm: bắt đầu từ đâu, thực hiện những bước nào, và kết quả cuối cùng cần có là gì?",
            Suggestions = new List<string>
            {
                "Đã có danh sách khóa học", "Bắt đầu từ nhu cầu đào tạo", "Đang theo dõi bằng Excel"
            }
        });

        Assert.True(reply.OpenEnded);
        Assert.Empty(reply.Suggestions);
    }

    // Cờ do BA tự đặt được tôn trọng kể cả khi câu hỏi không mang dấu hiệu nào parser nhận ra.
    [Fact]
    public void Normalize_DeclaredOpenEnded_DropsChipsEvenWithoutACue()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Quy trình hiện tại của anh/chị đang chạy ra sao?",
            Suggestions = new List<string> { "Trên giấy", "Bằng Excel" },
            MultiSelect = true,
            OpenEnded = true
        });

        Assert.True(reply.OpenEnded);
        Assert.Empty(reply.Suggestions);
        // multiSelect chỉ có nghĩa khi còn chip để tích.
        Assert.False(reply.MultiSelect);
    }

    // Guard chỉ chuyển ĐÓNG → MỞ. Câu đóng bình thường phải giữ nguyên chip: bỏ chip ở đây là bắt người
    // dùng nghiệp vụ gõ tay đúng thứ đáng lẽ bấm một cái là xong.
    [Theory]
    [InlineData("Khi đơn được duyệt hoặc từ chối, ai cần được báo?")]
    [InlineData("Áng chừng bao nhiêu người sẽ dùng ứng dụng này?")]
    // "thế nào" / "ra sao" KHÔNG phải dấu hiệu câu mở: các câu đào ngoại lệ dùng chúng nhiều nhất, mà
    // chip ở đó là những phương án TRỌN VẸN — chặn nhầm là hỏng đúng ca đang dùng tốt.
    [InlineData("Nếu đơn bị quản lý từ chối thì tiếp theo xử lý thế nào?")]
    // "kể cả" chứa "kể" nhưng không xin lời kể — lý do nhận diện bằng CỤM TỪ chứ không bằng từ đơn.
    [InlineData("Báo cáo có cần tính kể cả các lớp đã hủy không?")]
    public void Normalize_ClosedQuestion_KeepsItsChips(string question)
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = question,
            Suggestions = new List<string> { "Phương án A", "Phương án B" }
        });

        Assert.False(reply.OpenEnded);
        Assert.Equal(2, reply.Suggestions.Count);
    }

    // LỜI CẢM ƠN không phải LỜI XIN. Ca thật đã gặp trên màn hình (dự án quản lý đào tạo, lượt 1): BA
    // trả về một câu hỏi ĐÓNG kèm bốn chip vai trò, nhưng câu dẫn mở đầu bằng *"Cảm ơn anh/chị đã mô
    // tả."* — hai chữ "mô tả" ở đó nhắc lại điều NGƯỜI DÙNG vừa nói, không xin thêm lời kể nào. Guard
    // quét cả lượt nên bắt trúng nó, xoá sạch chip, và người dùng nhìn thấy một câu hỏi đóng không có
    // nút nào để bấm — trong khi AI Call Logs vẫn ghi đủ bốn gợi ý model trả về.
    [Theory]
    [InlineData("Cảm ơn anh/chị đã mô tả. Mình muốn hiểu rõ hơn về các vai trò sẽ dùng ứng dụng này. Anh/chị cho mình biết ứng dụng phục vụ những vai trò nào trong nhà máy?")]
    [InlineData("Như anh/chị vừa mô tả, lớp học có sĩ số tối thiểu. Ai được phép hủy lớp khi không đủ sĩ số?")]
    [InlineData("Theo mô tả của anh/chị thì đơn đi qua hai cấp duyệt. Cấp nào được phép trả lại đơn?")]
    public void Normalize_AcknowledgingWhatTheUserSaid_IsNotAskingForAStory(string message)
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = message,
            Suggestions = new List<string> { "Nhân viên", "Manager orgUnit", "HoD phòng ban", "HR – Đào tạo" },
            MultiSelect = true
        });

        Assert.False(reply.OpenEnded);
        Assert.Equal(4, reply.Suggestions.Count);
    }

    // …nhưng một lời cảm ơn ĐỨNG TRƯỚC một lời xin lời kể thì vẫn là xin lời kể: guard chỉ bỏ qua đúng
    // những lần cụm từ ấy nhắc lại chuyện cũ, không bỏ qua cả lượt vì có một lần như vậy.
    [Fact]
    public void Normalize_AcknowledgementFollowedByAStoryRequest_StaysOpenEnded()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Cảm ơn anh/chị đã mô tả. Anh/chị kể giúp mình lần gần nhất mở một lớp học thì làm những bước nào?",
            Suggestions = new List<string> { "Chọn khóa học", "Đặt phòng học" }
        });

        Assert.True(reply.OpenEnded);
        Assert.Empty(reply.Suggestions);
    }

    // Lượt KHÔNG phải câu hỏi (tóm tắt, lời mời bấm "Write Requirement") không bao giờ bị đánh dấu mở —
    // mời người dùng "kể tự do" ở một lượt không hỏi gì chỉ làm họ tưởng còn câu chưa trả lời.
    [Fact]
    public void Normalize_NonQuestionTurn_IsNeverOpenEnded()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mình đã mô tả lại toàn bộ yêu cầu ở trên. Nếu thấy đủ ý, anh/chị bấm nút \"Write Requirement\" để tạo tài liệu."
        });

        Assert.False(reply.OpenEnded);
    }

    // Đường model-trả-text: cùng một guard, vì hai đường vào phải cho ra cùng một màn hình.
    [Fact]
    public void Parse_TextPath_AppliesTheSameGuard()
    {
        var json = JsonSerializer.Serialize(new
        {
            message = "Anh/chị mô tả giúp mình quy trình đang chạy hiện nay?",
            suggestions = new[] { "Trên giấy", "Bằng Excel" },
            openEnded = false
        });

        var reply = _parser.Parse(json);

        Assert.True(reply.OpenEnded);
        Assert.Empty(reply.Suggestions);
    }

    // Cờ do model trả trong JSON được đọc đúng ở đường text.
    [Fact]
    public void Parse_TextPath_ReadsTheDeclaredFlag()
    {
        var json = JsonSerializer.Serialize(new
        {
            message = "Việc này hiện đang vướng nhất ở chỗ nào?",
            suggestions = Array.Empty<string>(),
            openEnded = true
        });

        Assert.True(_parser.Parse(json).OpenEnded);
    }

    // Từng câu trong lượt GỘP cũng qua guard: một dòng câu mở kèm chip hỏng đúng bằng lượt-đơn kèm chip,
    // chỉ nhẹ hơn ở chỗ thẻ gộp không gửi ngay khi bấm.
    [Fact]
    public void Normalize_BatchTurn_GuardsEachQuestionOnItsOwn()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mình hỏi nhanh mấy điểm sau nhé:",
            Questions = new List<BAChatQuestion>
            {
                new()
                {
                    Question = "Anh/chị mô tả giúp mình các bước lập kế hoạch hiện nay?",
                    Suggestions = new List<string> { "Bắt đầu từ nhu cầu", "Đã có danh sách khóa" }
                },
                new()
                {
                    Question = "Áng chừng bao nhiêu người sẽ dùng ứng dụng này?",
                    Suggestions = new List<string> { "Dưới 20 người", "20–100 người" }
                }
            }
        });

        Assert.True(reply.Questions[0].OpenEnded);
        Assert.Empty(reply.Questions[0].Suggestions);
        Assert.False(reply.Questions[1].OpenEnded);
        Assert.Equal(2, reply.Questions[1].Suggestions.Count);
    }

    // Lượt "gộp" chỉ có ĐÚNG MỘT câu được hạ về đường một-câu — cờ mở phải đi theo, không thì câu hỏi mất
    // chip (đúng) nhưng UI không biết đó là câu mở nên không mời người dùng kể.
    [Fact]
    public void Normalize_SingleQuestionBatch_CarriesTheFlagDownToTheSinglePath()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = string.Empty,
            Questions = new List<BAChatQuestion>
            {
                new()
                {
                    Question = "Anh/chị kể giúp mình lần gần nhất chốt kế hoạch năm?",
                    Suggestions = new List<string> { "Họp đầu năm", "Gửi email" }
                }
            }
        });

        Assert.Empty(reply.Questions);
        Assert.True(reply.OpenEnded);
        Assert.Empty(reply.Suggestions);
    }

    // Lượt GỘP không mang cờ ở cấp ngoài: mỗi dòng thẻ tự có ô nhập của nó, còn cờ cấp ngoài điều khiển
    // ô nhập chính của khung chat — bật lên là mời người dùng trả lời ở hai chỗ cho cùng một lượt.
    [Fact]
    public void Normalize_BatchTurn_NeverMarksTheOuterTurnOpenEnded()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Anh/chị mô tả giúp mình mấy điểm sau nhé?",
            OpenEnded = true,
            Questions = new List<BAChatQuestion>
            {
                new() { Question = "Ai cần được báo khi lớp bị hủy?", Suggestions = new List<string> { "Học viên", "Quản lý" } },
                new() { Question = "Bao nhiêu người sẽ dùng?", Suggestions = new List<string> { "Dưới 20", "Trên 20" } }
            }
        });

        Assert.False(reply.OpenEnded);
        Assert.Equal(2, reply.Questions.Count);
    }
}
