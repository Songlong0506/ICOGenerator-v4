using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// HAI NHÓM ĐÓNG ĐƯỢC BẰNG MỘT CÚ BẤM — «Luồng ngoại lệ & trường hợp đặc biệt» và «Báo cáo / thống kê».
//
// Chúng là hai nhóm duy nhất mà một câu phủ định của người dùng đưa dòng bản đồ thẳng tới
// [KHÔNG ÁP DỤNG] (requirement-coverage.v3.md), mà [KHÔNG ÁP DỤNG] thì không có đường quay lại: cổng
// readiness bỏ qua dòng đó và BA bị cấm hỏi lại. Nghĩa là một chip "Không có trường hợp đặc biệt" —
// bốn chữ — đóng vĩnh viễn đúng cái nhóm mà prompt gọi là lỗ hổng lớn nhất của tài liệu yêu cầu.
//
// Ca thật (dự án JD Libary 5, lượt 22–23): BA gộp ba câu vào một thẻ; câu đầu hỏi ngoại lệ với cặp chip
// có/không, câu cuối hỏi báo cáo cũng bằng cặp chip có/không. Người dùng bấm hai chip phủ định, và cả hai
// nhóm đóng lại tới hết buổi — trong khi chính hội thoại đó đã kể một đường hỏng ở lượt 9 (bị reject thì
// Manager sửa rồi submit lại) và hai điểm đau ở lượt 7 chính là một màn hình tra cứu.
//
// Hai luật, cả hai chỉ MỞ RỘNG chỗ trả lời:
//  1. Câu đào ngoại lệ phải đứng MỘT MÌNH trong lượt (prompt xếp nó vào "BẮT BUỘC hỏi MỘT MÌNH").
//  2. Cả hai nhóm không được hỏi bằng cặp chip có/không ⇒ bỏ chip, lượt thành câu MỞ.
public class InterviewQuestionRulesTests
{
    private readonly BAChatReplyParser _parser = new();

    private const string ExceptionGroup = "Luồng ngoại lệ & trường hợp đặc biệt";
    private const string ReportGroup = "Báo cáo / thống kê";

    private static BAChatQuestion Question(string group, string question, params string[] suggestions) => new()
    {
        Group = group,
        Question = question,
        Suggestions = suggestions.ToList()
    };

    // Lượt gộp nguyên văn của ca thật: ngoại lệ + quy mô + báo cáo trong một thẻ ba dòng.
    [Fact]
    public void AnExceptionQuestionInABatchTakesOverTheWholeTurn()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mình hỏi nhanh một số điểm còn lại nhé:",
            Questions = new List<BAChatQuestion>
            {
                Question(ExceptionGroup, "Khi gán JD cho nhân viên, có trường hợp nào cần xử lý đặc biệt không?",
                    "Không có trường hợp đặc biệt", "Có, để tôi mô tả"),
                Question("Quy mô sử dụng", "Áng chừng bao nhiêu người sẽ dùng ứng dụng này?",
                    "Dưới 50 người", "50–200 người"),
                Question(ReportGroup, "Anh/chị có cần báo cáo hay thống kê nào từ ứng dụng không?",
                    "Không cần báo cáo", "Có, để tôi mô tả")
            }
        });

        // Còn đúng một câu ⇒ Normalize hạ về đường một-câu: câu ngoại lệ được nối vào message.
        Assert.Empty(reply.Questions);
        Assert.Contains("có trường hợp nào cần xử lý đặc biệt không?", reply.Message);
        Assert.DoesNotContain("bao nhiêu người", reply.Message);
        Assert.DoesNotContain("báo cáo", reply.Message);

        // …và nó thành CÂU MỞ: cặp chip có/không không còn chỗ nào để kể một tình huống hỏng.
        Assert.True(reply.OpenEnded);
        Assert.Empty(reply.Suggestions);
    }

    // Không có câu ngoại lệ thì lượt gộp giữ nguyên — luật này chỉ nhắm đúng một nhóm, không phải một
    // cái phanh chung làm hẹp mọi lượt gộp.
    [Fact]
    public void ABatchWithoutAnExceptionQuestionIsLeftAlone()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mình hỏi thêm mấy điểm nhé:",
            Questions = new List<BAChatQuestion>
            {
                Question("Quy mô sử dụng", "Áng chừng bao nhiêu người sẽ dùng ứng dụng này?",
                    "Dưới 50 người", "50–200 người"),
                Question("Vòng đời & trạng thái", "Một JD đi qua những trạng thái nào?",
                    "Nháp", "Chờ duyệt", "Đã duyệt")
            }
        });

        Assert.Equal(2, reply.Questions.Count);
    }

    [Fact]
    public void AYesNoChipPairIsStrippedFromTheReportGroup()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mình hỏi thêm mấy điểm nhé:",
            Questions = new List<BAChatQuestion>
            {
                Question(ReportGroup, "Anh/chị có cần báo cáo hay thống kê nào từ ứng dụng không?",
                    "Không cần báo cáo", "Có, để tôi mô tả"),
                Question("Quy mô sử dụng", "Áng chừng bao nhiêu người sẽ dùng ứng dụng này?",
                    "Dưới 50 người", "50–200 người")
            }
        });

        var report = Assert.Single(reply.Questions, q => q.Group == ReportGroup);
        Assert.True(report.OpenEnded);
        Assert.Empty(report.Suggestions);

        // Câu nhóm khác trong cùng lượt không bị đụng tới.
        var scale = Assert.Single(reply.Questions, q => q.Group == "Quy mô sử dụng");
        Assert.Equal(2, scale.Suggestions.Count);
    }

    // Ranh giới: chỉ hình dạng CÓ/KHÔNG bị cấm, không phải mọi chip của hai nhóm này. Một bộ chip liệt kê
    // các loại báo cáo vẫn là cách hỏi tốt nhất cho người dùng nghiệp vụ.
    [Fact]
    public void AListingChipSetSurvivesInTheReportGroup()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mình hỏi thêm mấy điểm nhé:",
            Questions = new List<BAChatQuestion>
            {
                Question(ReportGroup, "Cấp quản lý cần xem những báo cáo nào?",
                    "JD theo orgUnit", "Nhân viên đang giữ JD nào", "JD chờ duyệt"),
                Question("Quy mô sử dụng", "Áng chừng bao nhiêu người sẽ dùng ứng dụng này?",
                    "Dưới 50 người", "50–200 người")
            }
        });

        var report = Assert.Single(reply.Questions, q => q.Group == ReportGroup);
        Assert.False(report.OpenEnded);
        Assert.Equal(3, report.Suggestions.Count);
    }

    // Ranh giới thứ hai, quan trọng hơn: bộ HAI chip ở lượt xin chốt (["Đúng rồi", "Không, tính khác"])
    // KHÔNG phải cặp có/không — vế "không" ở đó là một nhánh trả lời thật của câu hỏi chốt, và prompt kê
    // sẵn bộ đó. Xoá nó đi là biến một câu hỏi thành cái gật bắt buộc.
    [Fact]
    public void AConfirmationChipPairIsNotTreatedAsYesNo()
    {
        Assert.False(InterviewQuestionRules.IsYesNoPair(new[] { "Đúng rồi", "Không, tính khác" }));
        Assert.False(InterviewQuestionRules.IsYesNoPair(new[] { "Đúng luồng", "Không, khác" }));
        Assert.True(InterviewQuestionRules.IsYesNoPair(new[] { "Không có trường hợp đặc biệt", "Có, để tôi mô tả" }));
        Assert.True(InterviewQuestionRules.IsYesNoPair(new[] { "Có, cần lưu ngày hết hạn", "Không, chỉ cần ngày hiệu lực" }));

        // Ba chip trở lên thì không còn là cặp có/không.
        Assert.False(InterviewQuestionRules.IsYesNoPair(new[] { "Có", "Không", "Tùy trường hợp" }));
    }

    // Nhãn nhóm được so hai chiều bằng tiền tố: model viết ngắn hay đủ đều phải trúng, nếu không luật
    // này câm trong im lặng.
    [Fact]
    public void GroupLabelsMatchOnEitherPrefix()
    {
        Assert.True(InterviewQuestionRules.MustAskAlone("Luồng ngoại lệ"));
        Assert.True(InterviewQuestionRules.MustAskAlone("Luồng ngoại lệ & trường hợp đặc biệt"));
        Assert.False(InterviewQuestionRules.MustAskAlone("Chức năng & luồng nghiệp vụ chính"));
        Assert.False(InterviewQuestionRules.MustAskAlone(null));
    }
}
