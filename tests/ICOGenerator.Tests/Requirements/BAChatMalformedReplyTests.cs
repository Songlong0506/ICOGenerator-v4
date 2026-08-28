using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// KHUNG CHAT KHÔNG BAO GIỜ ĐƯỢC HIỆN RA MỘT KHỐI JSON.
//
// Ca thật (dự án JD Libary, lượt 6): model nhả `\u1E1y` — một dãy thoát rụng một chữ số hex — giữa chữ
// "vậy". Cả object không đọc được, parser rơi về nhánh "coi cả phản hồi là text hiển thị", và người dùng
// nhận nguyên khối `{"message":"Cảm ơn…","suggestions":[…],"ready":false}` làm lượt trả lời
// của BA. Thiệt hại không dừng ở một lượt xấu mặt: các tầng sau (chắt bản đồ bao phủ, nhật ký "Điều đã
// chốt") đọc khối đó như lời BA vừa nói ra, và lượt hỏi thật thì mất trắng.
//
// Hai lớp chặn, test ở đây khoá cả hai đầu ra mà người dùng nhìn thấy:
//   1. LlmJson sửa dãy thoát hỏng rồi đọc lại (xem LlmJsonTests) ⇒ lượt về nguyên vẹn, còn cả chip.
//   2. Sửa không xong thì parser VỚT lấy phần `message`; vớt không được thì trả lượt RỖNG để chốt chặn
//      lượt câm của BAChatService thay bằng câu hỏi tất định — không bao giờ rơi về "in cả khối JSON".
public class BAChatMalformedReplyTests
{
    private readonly BAChatReplyParser _parser = new();

    [Fact]
    public void ABrokenUnicodeEscape_StillYieldsTheRealMessageAndChips()
    {
        // Nguyên văn lượt 6 của dự án JD Libary, rút gọn: `\u1E1y` trong "như vậy".
        const string raw =
            """{"message":"Cảm ơn anh/chị. Đúng như v\u1E1y?","suggestions":["Đúng rồi","Không"],"multiSelect":false,"questions":[],"ready":false}""";

        var reply = _parser.Parse(raw);

        Assert.StartsWith("Cảm ơn anh/chị.", reply.Message);
        Assert.DoesNotContain("{", reply.Message);
        Assert.DoesNotContain("\\u", reply.Message);
        Assert.Equal(new[] { "Đúng rồi", "Không" }, reply.Suggestions);
    }

    // Chạm trần token giữa chuỗi: không có dấu " đóng, không có dấu } — LlmJson bó tay từ bước bóc object.
    [Fact]
    public void ATruncatedReply_IsSalvagedDownToItsMessage()
    {
        var reply = _parser.Parse("""{"message":"Anh/chị đang dùng công cụ gì?""");

        Assert.Equal("Anh/chị đang dùng công cụ gì?", reply.Message);
    }

    // Vớt không ra chữ nào ⇒ lượt RỖNG. BAChatService coi đó là lượt câm và thay bằng bước kế tiếp tất
    // định suy từ bản đồ bao phủ — một câu hỏi khô cứng vẫn hơn một khối JSON.
    [Fact]
    public void AJsonBlobWithNoUsableMessage_LeavesTheTurnEmpty()
    {
        var reply = _parser.Parse("""{"suggestions":["A","B"],"ready":false""");

        Assert.Equal(string.Empty, reply.Message);
    }

    // Phản hồi text thuần (model không theo JSON) vẫn đi nguyên văn như trước — nhánh vớt chỉ áp cho thứ
    // CÓ HÌNH DẠNG JSON.
    [Fact]
    public void PlainProseIsStillPassedThroughUntouched()
    {
        const string prose = "Anh/chị kể giúp mình quy trình hiện tại đang chạy thế nào?";

        Assert.Equal(prose, _parser.Parse(prose).Message);
    }
}
