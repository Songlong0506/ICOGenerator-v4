using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// CÂU HỎI MỞ thì KHÔNG có chip — và ai QUYẾT ĐỊNH một câu là mở.
//
// Vì sao luật tồn tại. Luật cũ bắt "mọi câu hỏi đều phải kèm gợi ý", nên BA hỏi xin một câu chuyện rồi
// vẫn dựng ra một hàng chip. Lỗi thật đã gặp trên màn hình:
//
//   "Anh/chị kể giúp một lần gần nhất lập kế hoạch cho các lớp học trong năm: bắt đầu từ đâu, thực hiện
//    những bước nào, và kết quả cuối cùng cần có là gì?"
//   ["Đã có danh sách khóa học", "Bắt đầu từ nhu cầu đào tạo", "Đang theo dõi bằng Excel", …]
//
// Bốn chip chỉ chạm vế "bắt đầu từ đâu". Ở lượt hỏi MỘT câu, bấm chip là GỬI NGAY — nên "các bước" và
// "kết quả cuối cùng", đúng hai thứ đắt nhất, không bao giờ được kể; rồi mẩu bốn chữ đó được chắt vào bản
// đồ bao phủ như câu trả lời thật của người dùng, và nhóm coi như đã hỏi xong.
//
// Ai quyết định. Cờ `openEnded` do MODEL đặt, đi thẳng lên màn hình như `suggestions` và `multiSelect`.
// Parser từng tự đoán thêm bằng một bảng cụm từ tiếng Việt (`LooksOpenEnded` / `NarrativeCues`: "kể giúp",
// "mô tả", "nói rõ hơn"…) và guard đó ĐÃ BỊ GỠ: đoán ngữ nghĩa bằng `Contains` không bao giờ phủ hết, mà
// mỗi lần đoán sai là XOÁ TRẮNG hàng chip của một lượt — hai ca thật, hai vòng vá, cùng đúng hai chữ "mô
// tả" ("Cảm ơn anh/chị đã mô tả.", rồi "…(ví dụ: mô tả vai trò, phòng ban áp dụng)…"), và mỗi ngôn ngữ
// mới lại là một bảng nữa. Xem docs/requirement-flow.md, mục "Câu ĐÓNG mới có chip".
//
// Ba bất biến giữ chỗ này:
//  1. `openEnded` ⇒ chip bị XÓA. Cờ và bộ chip không bao giờ cùng sống — UI chỉ có một chỗ trả lời.
//  2. Parser KHÔNG đoán cờ từ câu chữ. Không cụm từ nào, ở bất kỳ ngôn ngữ nào, tự xoá được hàng chip;
//     model quên đánh dấu thì chip ở lại (cái giá đã biết, lưới an toàn là điểm chấm trong golden set).
//  3. Áp ở CẢ hai đường vào (Parse cho model trả text, Normalize cho structured output) và cho CẢ câu
//     lượt-đơn lẫn từng câu trong lượt gộp.
public class BAChatOpenEndedQuestionTests
{
    private readonly BAChatReplyParser _parser = new();

    // Chính ca đã gặp trên màn hình: BA xin lời kể, đánh dấu đúng, nhưng vẫn kèm chip trả lời được một mẩu.
    [Fact]
    public void Normalize_StoryQuestionWithChips_DropsTheChips()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Anh/chị kể giúp một lần gần nhất lập kế hoạch cho các lớp học trong năm: bắt đầu từ đâu, thực hiện những bước nào, và kết quả cuối cùng cần có là gì?",
            Suggestions = new List<string>
            {
                "Đã có danh sách khóa học", "Bắt đầu từ nhu cầu đào tạo", "Đang theo dõi bằng Excel"
            },
            MultiSelect = true,
            OpenEnded = true
        });

        Assert.True(reply.OpenEnded);
        Assert.Empty(reply.Suggestions);
        // multiSelect chỉ có nghĩa khi còn chip để tích.
        Assert.False(reply.MultiSelect);
    }

    // Cờ do BA tự đặt được tôn trọng kể cả khi câu hỏi trông như một câu đóng bình thường.
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
    }

    // Câu đóng bình thường giữ nguyên chip: bỏ chip ở đây là bắt người dùng nghiệp vụ gõ tay đúng thứ
    // đáng lẽ bấm một cái là xong.
    [Theory]
    [InlineData("Khi đơn được duyệt hoặc từ chối, ai cần được báo?")]
    [InlineData("Áng chừng bao nhiêu người sẽ dùng ứng dụng này?")]
    [InlineData("Nếu đơn bị quản lý từ chối thì tiếp theo xử lý thế nào?")]
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

    // CÂU CHỮ KHÔNG BAO GIỜ XOÁ ĐƯỢC CHIP. Đây là bất biến thay cho guard đã gỡ, và mỗi dòng dưới đây là
    // một ca thật từng làm mất trắng hàng chip trên màn hình trong khi AI Call Logs vẫn ghi đủ gợi ý:
    //
    //  - "Cảm ơn anh/chị đã mô tả." — cụm chỉ NHẮC LẠI điều người dùng vừa nói, câu hỏi thật là câu đóng.
    //  - "(ví dụ: mô tả vai trò, phòng ban áp dụng)" — "mô tả" ở đây là TÊN MỘT CỘT DỮ LIỆU của vai trò.
    //
    // Hai vòng vá trước đều là thu hẹp bảng cụm từ, và cả hai đều để lọt vòng sau. Nay không còn bảng:
    // model không đặt cờ thì chip ở lại, dù câu có chứa cụm gì và viết bằng tiếng gì.
    [Theory]
    [InlineData("Cảm ơn anh/chị đã mô tả. Anh/chị cho mình biết ứng dụng phục vụ những vai trò nào trong nhà máy?")]
    [InlineData("Vậy với danh sách vai trò này, có cần quản lý thêm thông tin gì về vai trò không (ví dụ: mô tả vai trò, phòng ban áp dụng), hay chỉ cần tên vai trò là đủ?")]
    [InlineData("Anh/chị mô tả giúp mình quy trình đang chạy hiện nay?")]
    [InlineData("Could you describe how the current process runs?")]
    public void Normalize_WordingAlone_NeverDropsTheChips(string message)
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = message,
            Suggestions = new List<string> { "Phương án A", "Phương án B", "Phương án C" }
        });

        Assert.False(reply.OpenEnded);
        Assert.Equal(3, reply.Suggestions.Count);
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

    // Đường model-trả-text: cùng một luật, vì hai đường vào phải cho ra cùng một màn hình.
    [Fact]
    public void Parse_TextPath_AppliesTheSameRule()
    {
        var json = JsonSerializer.Serialize(new
        {
            message = "Anh/chị mô tả giúp mình quy trình đang chạy hiện nay?",
            suggestions = new[] { "Trên giấy", "Bằng Excel" },
            openEnded = true
        });

        var reply = _parser.Parse(json);

        Assert.True(reply.OpenEnded);
        Assert.Empty(reply.Suggestions);
    }

    // …và ở đúng câu đó, cờ `false` giữ nguyên chip: đường text cũng không đoán hộ model.
    [Fact]
    public void Parse_TextPath_KeepsChipsWhenTheFlagIsNotSet()
    {
        var json = JsonSerializer.Serialize(new
        {
            message = "Anh/chị mô tả giúp mình quy trình đang chạy hiện nay?",
            suggestions = new[] { "Trên giấy", "Bằng Excel" },
            openEnded = false
        });

        var reply = _parser.Parse(json);

        Assert.False(reply.OpenEnded);
        Assert.Equal(2, reply.Suggestions.Count);
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

    // Từng câu trong lượt GỘP mang cờ RIÊNG: một dòng câu mở kèm chip hỏng đúng bằng lượt-đơn kèm chip,
    // chỉ nhẹ hơn ở chỗ thẻ gộp không gửi ngay khi bấm. Dòng bên cạnh không bị ảnh hưởng.
    [Fact]
    public void Normalize_BatchTurn_AppliesTheFlagPerQuestion()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mình hỏi nhanh mấy điểm sau nhé:",
            Questions = new List<BAChatQuestion>
            {
                new()
                {
                    Question = "Anh/chị mô tả giúp mình các bước lập kế hoạch hiện nay?",
                    Suggestions = new List<string> { "Bắt đầu từ nhu cầu", "Đã có danh sách khóa" },
                    OpenEnded = true
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

    // …và một dòng KHÔNG mang cờ giữ nguyên chip dù câu chữ trông như lời xin lời kể.
    [Fact]
    public void Normalize_BatchTurn_WordingAloneNeverDropsAQuestionsChips()
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

        Assert.False(reply.Questions[0].OpenEnded);
        Assert.Equal(2, reply.Questions[0].Suggestions.Count);
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
                    Suggestions = new List<string> { "Họp đầu năm", "Gửi email" },
                    OpenEnded = true
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
