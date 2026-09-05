using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// BỘ CHIP ĐI THẲNG TỪ MODEL.
//
// Trước đây parser có `ShapeAnswer`: nó đọc CÂU HỎI bằng các bảng cụm từ tiếng Việt (`ListingCues`,
// `SinglePickCues`), đọc HÌNH DẠNG bộ chip bằng các bảng khác (`ExclusiveChipPrefixes`,
// `DependentChipPrefixes`, `BundleSeparators`), rồi khi hai bên chỏi nhau thì **xoá sạch hàng chip** và
// đổi lượt thành câu mở. Ý định thì đúng — chặn một câu trả lời tự mâu thuẫn đi thẳng vào bản đồ bao phủ
// — nhưng cơ chế thì không trả giá nổi:
//
//   - Đoán ngữ nghĩa tiếng Việt bằng `Contains` không bao giờ phủ hết, nên mỗi ca lọt lưới đẻ thêm một
//     dòng vào bảng cụm từ; 45% file parser từng là các bảng đó.
//   - Vì đoán sai là MẤT TRẮNG hàng chip, các bảng buộc phải chính xác — và đó là thứ không đạt được.
//     Ca thật khiến guard bị gỡ: BA hỏi *"Admin còn cần làm những việc gì khác không?"* với bốn chip
//     dùng được ngay ở chế độ chọn-một, nhưng chip đầu mở đầu bằng *"Chỉ …"* nên cả bộ bị coi là sai
//     hình dạng và bị xoá. Người dùng nhìn thấy một câu hỏi không có nút nào để bấm, trong khi AI Call
//     Logs ghi đủ bốn gợi ý model trả về.
//
// Nay parser KHÔNG phán đoán hình dạng nữa: `suggestions` và `multiSelect` của model lên thẳng màn hình.
// Thứ còn lại chỉ là những phép dọn không đoán ngữ nghĩa — cắt chip rỗng/trùng/quá dài, trần 6 chip, và
// một phép ĐẾM (dưới hai chip thì không có gì để "chọn nhiều") — cộng với chip "khác" trần, phép xoá DUY
// NHẤT còn được phép vì thứ bị xoá đã có sẵn ở ô "Ý khác" ngay dưới hàng chip.
//
// Cái giá đã biết và đã chấp nhận: model trả về một câu hỏi liệt kê kèm chip lồng nhau VÀ `multiSelect:
// true` thì UI cho tích hai ô mâu thuẫn nhau. Chỗ chặn ca đó nay là PROMPT (`requirement-chat.v4.md`,
// mục *"HAI KIỂU BỘ GỢI Ý"*) chứ không phải parser.
public class BAChatSuggestionChipTests
{
    private readonly BAChatReplyParser _parser = new();

    // ==== CHIP CỦA MODEL LÊN THẲNG MÀN HÌNH ====

    // Chính ca đã gỡ `ShapeAnswer`. Bộ chip trộn một phương án loại trừ (*"Chỉ …"*, có cả " và " bên
    // trong) với ba phương án cộng thêm — hình dạng mà guard cũ coi là không render được. Ở chế độ
    // chọn-một nó render hoàn hảo: bấm một chip là một câu trả lời trọn vẹn.
    [Fact]
    public void Normalize_MixedShapeChipsOnAListingQuestion_AreKeptNow()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Ngoài việc quản lý danh mục khóa học và gán khóa bắt buộc cho vai trò, "
                    + "Admin còn cần làm những việc gì khác trong ứng dụng không?",
            Suggestions = new List<string>
            {
                "Chỉ quản lý khóa học và gán khóa cho vai trò",
                "Còn quản lý cả danh mục vai trò",
                "Còn xem được báo cáo tổng hợp",
                "Còn quản lý thông tin nhân viên"
            }
        });

        Assert.Equal(4, reply.Suggestions.Count);
        Assert.False(reply.OpenEnded);
        Assert.False(reply.MultiSelect);
    }

    // Cờ của model được tôn trọng theo CẢ HAI chiều — đây đúng là thứ `ShapeAnswer` từng làm ngược:
    // nó tự bật cờ ở câu liệt kê chip nguyên tử, và tự hạ cờ ở bộ chip nó cho là sai hình dạng.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Normalize_ModelMultiSelectFlag_IsTrustedBothWays(bool multiSelect)
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Ứng dụng sẽ có những vai trò nào?",
            Suggestions = new List<string> { "Nhân viên và HR/đào tạo", "Chỉ bộ phận HR" },
            MultiSelect = multiSelect
        });

        Assert.Equal(multiSelect, reply.MultiSelect);
        Assert.Equal(2, reply.Suggestions.Count);
        Assert.False(reply.OpenEnded);
    }

    // Phép kiểm duy nhất còn sót lại quanh `multiSelect`, và nó là phép ĐẾM chứ không phải phán đoán:
    // một chip thì không có gì để tích, mà bật cờ ở đó dựng ra một hàng tick kèm nút "Gửi các lựa chọn"
    // cho đúng một ô.
    [Fact]
    public void Normalize_SingleChip_IsNeverMultiSelect()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Ứng dụng sẽ có những vai trò nào?",
            Suggestions = new List<string> { "Nhân viên" },
            MultiSelect = true
        });

        Assert.False(reply.MultiSelect);
        Assert.Single(reply.Suggestions);
    }

    // Đường model-trả-text đi qua đúng bộ luật đó — hai đường vào phải cho ra cùng một màn hình.
    [Fact]
    public void Parse_TextPath_KeepsTheChipsAndTheFlag()
    {
        var json = JsonSerializer.Serialize(new
        {
            message = "Ứng dụng sẽ có những vai trò nào?",
            suggestions = new[] { "Nhân viên và HR/đào tạo", "Chỉ bộ phận HR/đào tạo" },
            multiSelect = true
        });

        var reply = _parser.Parse(json);

        Assert.True(reply.MultiSelect);
        Assert.Equal(2, reply.Suggestions.Count);
    }

    // Chip của TỪNG câu trong lượt gộp cũng đi thẳng: mỗi dòng thẻ hỏi giữ nguyên bộ chip và cờ model đặt.
    [Fact]
    public void Normalize_BatchTurn_KeepsEachRowsChipsAndFlag()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mình hỏi nhanh mấy điểm sau nhé:",
            Questions = new List<BAChatQuestion>
            {
                new()
                {
                    Question = "Ứng dụng sẽ có những vai trò nào?",
                    Suggestions = new List<string> { "Nhân viên và HR", "Chỉ bộ phận HR" },
                    MultiSelect = true
                },
                new()
                {
                    Question = "Cần những loại báo cáo nào?",
                    Suggestions = new List<string> { "Tỉ lệ hoàn thành", "Chi phí đào tạo" }
                }
            }
        });

        Assert.True(reply.Questions[0].MultiSelect);
        Assert.Equal(2, reply.Questions[0].Suggestions.Count);
        Assert.False(reply.Questions[1].MultiSelect);
        Assert.Equal(2, reply.Questions[1].Suggestions.Count);
    }

    // Câu hỏi ép chọn ĐÚNG MỘT và câu hỏi liệt kê nay đi chung một đường — parser không còn phân biệt.
    [Fact]
    public void Normalize_NonEnumerationQuestion_KeepsItsChips()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Nếu đơn bị quản lý từ chối thì tiếp theo xử lý thế nào?",
            Suggestions = new List<string> { "Nhân viên sửa rồi gửi lại", "Hủy hẳn đơn", "Chuyển cấp cao hơn duyệt" }
        });

        Assert.False(reply.MultiSelect);
        Assert.False(reply.OpenEnded);
        Assert.Equal(3, reply.Suggestions.Count);
    }

    // Chip "chốt hạ" (*"Tất cả các việc trên"*) từng bị xoá như một phần của `ShapeAnswer`. Nay nó SỐNG:
    // ở chế độ chọn-một — chế độ mặc định — nó là một câu trả lời hợp lệ và đủ nghĩa.
    [Fact]
    public void Normalize_AllOfTheAboveChip_SurvivesNow()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Trong ứng dụng, Nhân viên sẽ chịu trách nhiệm thực hiện những việc gì?",
            Suggestions = new List<string>
            {
                "Xem khóa học được giao",
                "Đăng ký khóa tự chọn",
                "Tất cả các việc trên"
            }
        });

        Assert.Equal(3, reply.Suggestions.Count);
        Assert.Contains("Tất cả các việc trên", reply.Suggestions);
    }

    // ==== CHIP "KHÁC" TRẦN ====
    // Ca thật trên màn hình: câu hỏi về trạng thái của record khi một bên chưa ký, ba chip đầu là phương
    // án thật, chip thứ tư là "Quy tắc khác" — trong khi ngay dưới hàng chip đã có ô "Ý khác" luôn mở.
    // Chip đó nói đúng bằng cái ô mà không chở nội dung, và ở lượt một câu thì bấm là GỬI NGAY: người
    // dùng gửi đi một lượt rỗng, còn bản đồ bao phủ tính là nhóm đã hỏi xong.
    //
    // Prompt cấm chip này từ lâu nhưng cấm theo MẶT CHỮ ("Khác", "Tự nhập"), nên model né được chỉ bằng
    // cách thêm một danh từ vào trước. Parser cấm theo HÌNH DẠNG của riêng CHIP đó, và cấm ở MỌI câu —
    // nó không cần biết câu hỏi thuộc kiểu gì, đó là lý do nó sống sót khi ShapeAnswer bị gỡ.
    private const string SigningStateQuestion =
        "Nếu một trong ba bên chưa ký thì record giữ ở trạng thái nào?";

    [Fact]
    public void Normalize_BareOtherChip_IsDroppedBecauseTheOtherBoxAlreadySaysIt()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = SigningStateQuestion,
            Suggestions = new List<string>
            {
                "Vẫn giữ Waiting Active",
                "Chuyển sang trạng thái Chờ ký",
                "HRBP nhắc người chưa ký",
                "Quy tắc khác"
            }
        });

        Assert.Equal(3, reply.Suggestions.Count);
        Assert.DoesNotContain("Quy tắc khác", reply.Suggestions);
        Assert.False(reply.OpenEnded);
    }

    // Cùng một chip đội nhiều tên — đó chính là lý do phải bắt theo hình dạng thay vì liệt kê mặt chữ.
    [Theory]
    [InlineData("Khác")]
    [InlineData("Tự nhập")]
    [InlineData("Ý khác")]
    [InlineData("Trạng thái khác")]
    [InlineData("Cách xử lý khác")]
    [InlineData("Phương án khác")]
    [InlineData("Trường hợp khác")]
    [InlineData("Khác (tự nhập)")]
    [InlineData("Quy tắc khác…")]
    public void Normalize_EveryDisguiseOfTheBareOtherChip_IsDropped(string escapeHatch)
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = SigningStateQuestion,
            Suggestions = new List<string> { "Vẫn giữ Waiting Active", "Chuyển sang trạng thái Chờ ký", escapeHatch }
        });

        Assert.Equal(2, reply.Suggestions.Count);
        Assert.DoesNotContain(escapeHatch, reply.Suggestions);
    }

    // Đuôi "khác" KHÔNG đủ để xoá: phần đầu phải là một danh từ mê-ta. "Chuyển sang phòng ban khác" là một
    // phương án có thật, nuốt nó đi là mất một câu trả lời mà không ai phát hiện được.
    [Fact]
    public void Normalize_ChipEndingInOtherButCarryingRealContent_Survives()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = SigningStateQuestion,
            Suggestions = new List<string>
            {
                "Vẫn giữ Waiting Active",
                "Chuyển sang trạng thái Chờ ký",
                "Chuyển sang phòng ban khác"
            }
        });

        Assert.Equal(3, reply.Suggestions.Count);
        Assert.Contains("Chuyển sang phòng ban khác", reply.Suggestions);
    }

    // Bộ HAI chip ở lượt xin chốt: vế "khác" là một trong hai NHÁNH TRẢ LỜI, không phải lối thoát. Xoá nó
    // là biến câu hỏi thành cái gật bắt buộc — nên ràng buộc "xoá xong còn ≥ 2 chip" giữ nguyên cả bộ, và
    // việc mở ô nhập tại chỗ để lại cho giao diện (requirements.js: isDissentChip).
    [Theory]
    [InlineData("Đồng ý", "Tôi muốn khác")]
    [InlineData("Đúng rồi", "Không, tính khác")]
    public void Normalize_TwoChipConfirmSet_KeepsTheDissentBranch(string agree, string dissent)
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Vậy mình chốt: chưa đủ ba chữ ký thì record vẫn ở Waiting Active nhé?",
            Suggestions = new List<string> { agree, dissent }
        });

        Assert.Equal(2, reply.Suggestions.Count);
        Assert.Contains(dissent, reply.Suggestions);
    }

    // Thêm một phương án thật vào bộ đó thì vế "khác" lại thành chip thừa: ô "Ý khác" vẫn ở đó, mà câu hỏi
    // giờ đã có hai nhánh trả lời không cần nó.
    [Fact]
    public void Normalize_ThreeChipSetWithADissentChip_DropsIt()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Vậy mình chốt: chưa đủ ba chữ ký thì record vẫn ở Waiting Active nhé?",
            Suggestions = new List<string> { "Đồng ý", "Chuyển sang Chờ ký", "Tôi muốn khác" }
        });

        Assert.Equal(2, reply.Suggestions.Count);
        Assert.DoesNotContain("Tôi muốn khác", reply.Suggestions);
    }

    // ==== CHIP TỰ-MÔ-TẢ ====
    // Cùng cái lối thoát đó nhưng KHÔNG mang chữ "khác" nào: "Mình mô tả cụ thể hơn". Ca thật trên màn
    // hình, và nó đến từ chính ví dụ JSON mẫu của prompt — model chép ví dụ nhiều hơn đọc mục cấm. Nó
    // nguy hơn bản có chữ "khác" ở chỗ đọc như một phương án tử tế, nên người dùng bấm mà không thấy mình
    // vừa gửi đi một lượt rỗng, còn bản đồ bao phủ tính là nhóm đã hỏi VÀ đã trả lời.
    [Theory]
    [InlineData("Mình mô tả cụ thể hơn")]
    [InlineData("Để tôi kể rõ hơn")]
    [InlineData("Tôi sẽ nói rõ hơn ở dưới")]
    [InlineData("Mình tự nhập")]
    [InlineData("Mình trình bày thêm")]
    public void Normalize_SelfDescribeChip_IsDroppedLikeTheBareOtherChip(string selfDescribe)
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = SigningStateQuestion,
            Suggestions = new List<string> { "Vẫn giữ Waiting Active", "Chuyển sang trạng thái Chờ ký", selfDescribe }
        });

        Assert.Equal(2, reply.Suggestions.Count);
        Assert.DoesNotContain(selfDescribe, reply.Suggestions);
    }

    // Phép thử đòi ĐỦ HAI vế — ngôi thứ nhất mở đầu VÀ động từ diễn đạt — chính là để những chip này sống.
    // Thiếu vế nào cũng đủ để một câu trả lời thật bị nuốt mà không ai phát hiện được: "Mô tả công việc
    // theo vai trò" là nghiệp vụ JD chứ không phải lối thoát, "Mình tự đăng ký khóa học" cũng vậy.
    [Theory]
    [InlineData("Mô tả công việc theo vai trò")]
    [InlineData("Mình tự đăng ký khóa học")]
    [InlineData("Quản lý mô tả lại quy trình cho nhân viên")]
    public void Normalize_ChipLookingLikeSelfDescribeButCarryingRealContent_Survives(string real)
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = SigningStateQuestion,
            Suggestions = new List<string> { "Vẫn giữ Waiting Active", "Chuyển sang trạng thái Chờ ký", real }
        });

        Assert.Equal(3, reply.Suggestions.Count);
        Assert.Contains(real, reply.Suggestions);
    }

    // "Tôi muốn sửa lại" có ngôi thứ nhất nhưng không có động từ diễn đạt nào ⇒ không chạm luật tự-mô-tả.
    // Đây là vế từ chối của nhịp tóm tắt kiểm chứng (BAChatService.SummaryCheckSuggestions): xoá nó là
    // biến lượt tóm tắt thành cái gật bắt buộc, kể cả khi bộ chip có nhiều hơn hai chip.
    [Fact]
    public void Normalize_SummaryCheckDissentChip_IsNotMistakenForSelfDescribe()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mình tóm tắt lại cách hiểu của mình, anh/chị xem giúp có đúng không?",
            Suggestions = new List<string> { "Đúng rồi, tiếp tục", "Tôi muốn bổ sung", "Tôi muốn sửa lại" }
        });

        Assert.Equal(3, reply.Suggestions.Count);
        Assert.Contains("Tôi muốn sửa lại", reply.Suggestions);
    }

    // Chốt thứ hai vẫn là "xoá xong còn ≥ 2 chip": bộ HAI chip không bao giờ bị bào thành một nút.
    [Fact]
    public void Normalize_TwoChipSetWithASelfDescribeBranch_KeepsBoth()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Vậy mình chốt: chưa đủ ba chữ ký thì record vẫn ở Waiting Active nhé?",
            Suggestions = new List<string> { "Đồng ý", "Mình mô tả cụ thể hơn" }
        });

        Assert.Equal(2, reply.Suggestions.Count);
        Assert.Contains("Mình mô tả cụ thể hơn", reply.Suggestions);
    }

    // Xoá chip "khác" trần KHÔNG đụng tới cờ chọn-nhiều của model: phép xoá là phép xoá, không phải một
    // phán đoán về hình dạng bộ chip.
    [Fact]
    public void Normalize_BareOtherChipInAMultiSelectSet_IsDroppedWithoutTouchingTheFlag()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Record đi qua những trạng thái nào?",
            Suggestions = new List<string> { "Waiting Active", "Active", "Đã thu hồi", "Trạng thái khác" },
            MultiSelect = true
        });

        Assert.True(reply.MultiSelect);
        Assert.Equal(3, reply.Suggestions.Count);
        Assert.DoesNotContain("Trạng thái khác", reply.Suggestions);
    }

    // Chip của từng câu trong lượt gộp đi qua cùng phép dọn — sót đường này là guard vắng mặt ở đúng
    // nửa số màn hình.
    [Fact]
    public void Normalize_BatchTurn_DropsTheBareOtherChipOfEachRow()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mình hỏi nhanh mấy điểm sau nhé:",
            Questions = new List<BAChatQuestion>
            {
                new()
                {
                    Question = SigningStateQuestion,
                    Suggestions = new List<string> { "Vẫn giữ Waiting Active", "Chuyển sang Chờ ký", "Quy tắc khác" }
                },
                // Lượt gộp phải có ≥ 2 câu, nếu không Normalize hạ nó về đường một-câu và test đo nhầm chỗ.
                new()
                {
                    Question = "Ai được thu hồi JD đã assign?",
                    Suggestions = new List<string> { "Manager tạo JD", "HRBP" }
                }
            }
        });

        Assert.Equal(2, reply.Questions[0].Suggestions.Count);
        Assert.DoesNotContain("Quy tắc khác", reply.Questions[0].Suggestions);
        Assert.False(reply.Questions[0].OpenEnded);
    }

    // Đường model-trả-text cũng vậy: hai đường vào phải cho ra cùng một màn hình.
    [Fact]
    public void Parse_TextPath_DropsTheBareOtherChipToo()
    {
        var json = JsonSerializer.Serialize(new
        {
            message = SigningStateQuestion,
            suggestions = new[] { "Vẫn giữ Waiting Active", "Chuyển sang Chờ ký", "Quy tắc khác" }
        });

        var reply = _parser.Parse(json);

        Assert.Equal(2, reply.Suggestions.Count);
        Assert.DoesNotContain("Quy tắc khác", reply.Suggestions);
    }
}
