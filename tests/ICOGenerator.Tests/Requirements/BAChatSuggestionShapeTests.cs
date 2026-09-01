using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// HÌNH DẠNG BỘ CHIP phải khớp với cờ multiSelect.
//
// Một bộ gợi ý chỉ thuộc đúng MỘT trong hai kiểu:
//   - PHƯƠNG ÁN THAY THẾ: mỗi chip là câu trả lời trọn vẹn, chọn cái này là loại cái kia ⇒ chọn MỘT.
//   - LIỆT KÊ THÀNH PHẦN: câu trả lời thật là một danh sách, mỗi chip là MỘT MẢNH ⇒ chọn NHIỀU.
//
// Lỗi thật đã gặp trên màn hình: BA hỏi "gồm những vai trò nào?" — đúng kiểu liệt kê, nên bật
// multiSelect — nhưng chip lại giữ dạng GÓI vai trò lồng nhau và phủ định nhau
// (["Nhân viên và HR/đào tạo", "Nhân viên, quản lý và HR", "Thêm HoD phòng ban",
// "Chỉ bộ phận HR/đào tạo"]). UI cho tích ô 1 + ô 4 cùng lúc, và cái gửi đi là một câu trả lời tự mâu
// thuẫn — được chắt thẳng vào bản đồ bao phủ như lời người dùng, nơi không tầng nào
// phía sau (Product Brief, spec, POC) còn phân biệt được nữa.
//
// Hạ cờ multiSelect chặn được câu trả lời tự mâu thuẫn, nhưng KHÔNG chặn được thiệt hại: câu hỏi liệt kê
// vẫn lên màn hình dưới dạng chọn-một, và model vá chỗ đó bằng một chip CHỐT HẠ ("Tất cả các việc trên").
// Người dùng bấm đúng cái chip ấy cho nhanh, bản đồ bao phủ ghi một cụm mờ thay vì bốn trách nhiệm rời —
// cùng một thiệt hại, đi bằng cửa khác. Nên tín hiệu quyết định không phải cờ, mà là CÂU HỎI.
//
// Bốn bất biến giữ chỗ này:
//  1. CÂU HỎI quyết định hình dạng. Câu liệt kê ("gồm những … nào?", "… những việc gì?") có đáp án là một
//     DANH SÁCH; không cờ nào biến nó thành câu chọn-một.
//  2. Ở câu liệt kê, chip chốt hạ bị XOÁ (nội dung của nó chính là các chip còn lại — xoá không mất gì),
//     rồi nếu phần còn lại nguyên tử và còn ≥ 2 chip thì multiSelect được BẬT, kể cả khi model để false:
//     trên bộ chip nguyên tử-rời nhau, mọi tổ hợp tích đều có nghĩa nên bật nhầm không sinh dữ liệu sai.
//  3. Ở câu liệt kê mà chip vẫn là phương án lắp sẵn, KHÔNG có hình dạng nào đúng để render ⇒ bỏ chip,
//     chuyển thành CÂU MỞ. Máy không được tự tách/viết lại chip (bịa từ thay BA) cũng không được xoá lẻ
//     (mất một mảnh khỏi bản đồ bao phủ mà không ai biết). Bắt gõ tay chỉ mất tiện ích, không mất dữ liệu.
//  4. Áp ở CẢ hai đường vào (Parse cho model trả text, Normalize cho structured output) và cho CẢ chip
//     lượt-đơn lẫn chip của từng câu trong lượt gộp — sót một đường là guard vắng mặt đúng chỗ nó cần.
//
// Câu KHÔNG phải liệt kê giữ nguyên luật cũ: cờ do BA đặt, hạ nếu bộ chip sai hình dạng, chip giữ nguyên.
public class BAChatSuggestionShapeTests
{
    private readonly BAChatReplyParser _parser = new();

    private static BAChatReply Single(bool multiSelect, params string[] suggestions) => new()
    {
        Message = "Ứng dụng sẽ có những vai trò nào?",
        Suggestions = suggestions.ToList(),
        MultiSelect = multiSelect
    };

    // Chính ca đã gặp trên màn hình. Câu hỏi đòi một danh sách vai trò, nhưng bốn chip là bốn GÓI lồng
    // nhau — không hình dạng nào render đúng được, kể cả chọn-một (bấm một gói là chốt một tổ hợp người
    // dùng chưa từng ghép). Lối ra duy nhất không sinh dữ liệu sai: mời họ tự kể.
    [Fact]
    public void Normalize_EnumerationQuestionWithBundledChips_BecomesAnOpenQuestion()
    {
        var reply = _parser.Normalize(Single(true,
            "Nhân viên và HR/đào tạo",
            "Nhân viên, quản lý và HR",
            "Thêm HoD phòng ban",
            "Chỉ bộ phận HR/đào tạo"));

        Assert.False(reply.MultiSelect);
        Assert.True(reply.OpenEnded);
        Assert.Empty(reply.Suggestions);
    }

    // Ca trong ảnh chụp màn hình: "… chịu trách nhiệm những việc gì?" — câu liệt kê, nhưng bộ chip trộn
    // một chip GÓI ("Tham gia và cập nhật kết quả") với một chip CHỐT HẠ ("Tất cả các việc trên"). Chính
    // chip chốt hạ là dấu hiệu model đang nghĩ theo kiểu chọn-một cho một câu hỏi vốn không chọn-một.
    [Fact]
    public void Normalize_ResponsibilityListWithAllOfTheAboveChip_BecomesAnOpenQuestion()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Trong ứng dụng, Nhân viên sẽ chịu trách nhiệm thực hiện những việc gì?",
            Suggestions = new List<string>
            {
                "Xem khóa học được giao",
                "Đăng ký khóa tự chọn",
                "Tham gia và cập nhật kết quả",
                "Tất cả các việc trên"
            }
        });

        Assert.False(reply.MultiSelect);
        Assert.True(reply.OpenEnded);
        Assert.Empty(reply.Suggestions);
    }

    // Cùng câu hỏi đó với chip đã viết nguyên tử: chip chốt hạ bị xoá (tích hết các ô ĐÃ là "tất cả"),
    // phần còn lại lên chọn nhiều — đây là màn hình đúng mà ca trên đang nhắm tới.
    [Fact]
    public void Normalize_AtomicChipsPlusAnAllOfTheAboveChip_DropsItAndEnablesMultiSelect()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Trong ứng dụng, Nhân viên sẽ chịu trách nhiệm thực hiện những việc gì?",
            Suggestions = new List<string>
            {
                "Xem khóa học được giao",
                "Đăng ký khóa tự chọn",
                "Tham gia lớp",
                "Cập nhật kết quả học",
                "Tất cả các việc trên"
            }
        });

        Assert.True(reply.MultiSelect);
        Assert.False(reply.OpenEnded);
        Assert.Equal(4, reply.Suggestions.Count);
        Assert.DoesNotContain("Tất cả các việc trên", reply.Suggestions);
    }

    // Chip chốt hạ nhận diện bằng CẢ HAI đầu: mở đầu chỉ toàn thể VÀ kết bằng tham chiếu ngược. Hai test
    // dưới đây khoá đúng hai kiểu xoá nhầm mà việc chỉ xét một đầu sẽ gây ra.
    [Fact]
    public void Normalize_ChipEndingInABackReferenceWord_IsARealRoleAndSurvives()
    {
        var reply = _parser.Normalize(Single(true, "Nhân viên", "HR – Đào tạo", "Cấp trên"));

        Assert.True(reply.MultiSelect);
        Assert.Contains("Cấp trên", reply.Suggestions);
    }

    // "Tất cả nhân viên nhà máy" mở đầu bằng "tất cả" nhưng là một nhóm người có thật, không tự tham chiếu
    // ⇒ không được xoá. Nó vẫn là chip LOẠI TRỪ nên bộ chip sai hình dạng và câu chuyển sang mở — mất cả
    // hàng chip thì rõ ràng, còn lặng lẽ nuốt đúng một chip mới là kiểu hỏng không ai phát hiện được.
    [Fact]
    public void Normalize_TotalityChipThatIsARealGroup_IsNeverSilentlyDropped()
    {
        var reply = _parser.Normalize(Single(true, "Nhân viên", "HR – Đào tạo", "Tất cả nhân viên nhà máy"));

        Assert.True(reply.OpenEnded);
        Assert.Empty(reply.Suggestions);
    }

    [Fact]
    public void Normalize_AtomicRoleChips_KeepMultiSelect()
    {
        var reply = _parser.Normalize(Single(true,
            "Nhân viên", "Manager orgUnit", "HoD phòng ban", "HR – Đào tạo"));

        Assert.True(reply.MultiSelect);
    }

    // Chip có tiền tố chung ("Báo cáo …") vẫn là chip nguyên tử — mỗi cái nêu đúng một thứ. Đây là dạng
    // hợp lệ phổ biến nhất của câu hỏi liệt kê, chặn nhầm nó là hỏng đúng ca đang dùng tốt.
    [Fact]
    public void Normalize_ChipsSharingACommonPrefix_KeepMultiSelect()
    {
        var reply = _parser.Normalize(Single(true,
            "Báo cáo tỉ lệ hoàn thành", "Báo cáo chi phí đào tạo", "Báo cáo theo phòng ban"));

        Assert.True(reply.MultiSelect);
    }

    // Một chip sai hình dạng là đủ để cả bộ hỏng: ở câu liệt kê thì không có cách render nào đúng, nên
    // lượt đó thành câu mở. (Chip "Cả hai bên trên" KHÔNG nằm ở đây — nó tự tham chiếu nên bị xoá rồi
    // phần còn lại vẫn dùng được; xem test chip chốt hạ ở trên.)
    [Theory]
    // (1) chip LOẠI TRỪ: tự nó bao hàm hoặc phủ định phần còn lại.
    [InlineData("Chỉ bộ phận HR")]
    [InlineData("Tất cả nhân viên nhà máy")]
    [InlineData("Không cần thông báo cho ai")]
    // (2) chip KHÔNG TỰ ĐỨNG: chỉ có nghĩa khi đọc kèm chip khác.
    [InlineData("Thêm HoD phòng ban")]
    // (3) chip GÓI: nêu từ hai thứ trở lên trong một dòng.
    [InlineData("Nhân viên và HoD")]
    [InlineData("Nhân viên, HR")]
    public void Normalize_EnumerationQuestion_AnySingleOffendingChip_BecomesAnOpenQuestion(string offending)
    {
        var reply = _parser.Normalize(Single(true, "Nhân viên", "HR – Đào tạo", offending));

        Assert.False(reply.MultiSelect);
        Assert.True(reply.OpenEnded);
    }

    // Dấu "/" thường là một cái TÊN ("HR/đào tạo", "TEF3.3/LL06"), không phải liệt kê hai thứ.
    [Fact]
    public void Normalize_SlashInsideAChipName_IsNotTreatedAsABundle()
    {
        var reply = _parser.Normalize(Single(true, "HR/đào tạo", "Nhân viên", "HoD phòng ban"));

        Assert.True(reply.MultiSelect);
    }

    // Model QUÊN cờ ở một câu liệt kê là ca hỏng phổ biến nhất, và cũng là ca đẻ ra chip chốt hạ. Câu hỏi
    // đã nói rõ đáp án là một danh sách còn chip thì nguyên tử-rời nhau: hai tín hiệu độc lập cùng chỉ một
    // hướng, mạnh hơn một cờ bị bỏ trống, nên bật. Ở bộ chip nguyên tử, mọi tổ hợp tích đều có nghĩa —
    // bật nhầm không sinh ra được câu trả lời tự mâu thuẫn nào.
    [Fact]
    public void Normalize_EnumerationQuestionWithAtomicChips_EnablesMultiSelectEvenWithoutTheFlag()
    {
        var reply = _parser.Normalize(Single(false, "Nhân viên", "Manager orgUnit", "HoD phòng ban"));

        Assert.True(reply.MultiSelect);
    }

    // Ngược lại, câu KHÔNG phải liệt kê vẫn để BA toàn quyền: chọn-một trên một bộ chip nguyên tử là một
    // phán đoán nghiệp vụ hợp lệ (bắt người dùng chốt đúng một phương án), không phải lỗi cần chữa.
    [Fact]
    public void Normalize_NonEnumerationQuestionWithAtomicChips_HonoursTheFlag()
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

    // Câu ép chọn ĐÚNG MỘT vẫn mang dạng số nhiều ("trong những … nào phù hợp nhất?"). Bắt nhầm nó thành
    // câu liệt kê là phá đúng cái nhịp chốt phương án mà BA đang cần.
    [Fact]
    public void Normalize_PluralPhrasedButSinglePickQuestion_IsNotTreatedAsEnumeration()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Trong những cách sau, cách nào phù hợp nhất với anh/chị?",
            Suggestions = new List<string> { "Nhập tay trên web", "Nhập từ file Excel", "Đồng bộ từ SAP" }
        });

        Assert.False(reply.MultiSelect);
        Assert.False(reply.OpenEnded);
    }

    // Một chip thì không có gì để "chọn nhiều".
    [Fact]
    public void Normalize_SingleChip_IsNeverMultiSelect()
    {
        var reply = _parser.Normalize(Single(true, "Nhân viên"));

        Assert.False(reply.MultiSelect);
    }

    // Đường model-trả-text: cùng một guard, vì hai đường vào phải cho ra cùng một màn hình.
    [Fact]
    public void Parse_TextPath_AppliesTheSameShapeGuard()
    {
        var json = JsonSerializer.Serialize(new
        {
            message = "Ứng dụng sẽ có những vai trò nào?",
            suggestions = new[] { "Nhân viên và HR/đào tạo", "Chỉ bộ phận HR/đào tạo" },
            multiSelect = true
        });

        Assert.False(_parser.Parse(json).MultiSelect);
    }

    // Chip của TỪNG câu trong lượt gộp cũng phải qua guard — mỗi dòng thẻ hỏi có hàng chip riêng, và
    // một dòng sai hình dạng thì hỏng đúng bằng chip lượt-đơn sai hình dạng.
    [Fact]
    public void Normalize_BatchTurn_GuardsEachQuestionsOwnChips()
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
                    Suggestions = new List<string> { "Tỉ lệ hoàn thành", "Chi phí đào tạo" },
                    MultiSelect = true
                }
            }
        });

        Assert.False(reply.Questions[0].MultiSelect);
        Assert.True(reply.Questions[1].MultiSelect);
    }

    // Cả hai nhánh mới cũng phải sống ở lượt gộp, nếu không thì đúng cùng một lỗi lại lọt qua đường kia.
    [Fact]
    public void Normalize_BatchTurn_AppliesTheQuestionDrivenShapeToEachRow()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Mình hỏi nhanh mấy điểm sau nhé:",
            Questions = new List<BAChatQuestion>
            {
                // Chip chốt hạ bị xoá, phần còn lại lên chọn nhiều dù model không đặt cờ.
                new()
                {
                    Question = "Nhân viên chịu trách nhiệm những việc gì?",
                    Suggestions = new List<string> { "Xem khóa học được giao", "Đăng ký khóa tự chọn", "Tất cả các việc trên" }
                },
                // Chip gói ở câu liệt kê ⇒ bỏ chip, dòng này thành câu mở.
                new()
                {
                    Question = "Cần theo dõi những chỉ số nào?",
                    Suggestions = new List<string> { "Tỉ lệ hoàn thành và chi phí", "Chỉ số hài lòng" },
                    MultiSelect = true
                }
            }
        });

        Assert.True(reply.Questions[0].MultiSelect);
        Assert.Equal(2, reply.Questions[0].Suggestions.Count);
        Assert.DoesNotContain("Tất cả các việc trên", reply.Questions[0].Suggestions);

        Assert.True(reply.Questions[1].OpenEnded);
        Assert.Empty(reply.Questions[1].Suggestions);
    }

    // ==== CHIP "KHÁC" TRẦN ====
    // Ca thật trên màn hình: câu hỏi về trạng thái của record khi một bên chưa ký, ba chip đầu là phương
    // án thật, chip thứ tư là "Quy tắc khác" — trong khi ngay dưới hàng chip đã có ô "Ý khác" luôn mở.
    // Chip đó nói đúng bằng cái ô mà không chở nội dung, và ở lượt một câu thì bấm là GỬI NGAY: người
    // dùng gửi đi một lượt rỗng, còn bản đồ bao phủ tính là nhóm đã hỏi xong.
    //
    // Prompt cấm chip này từ lâu nhưng cấm theo MẶT CHỮ ("Khác", "Tự nhập"), nên model né được chỉ bằng
    // cách thêm một danh từ vào trước. Parser cấm theo HÌNH DẠNG, và cấm ở MỌI câu chứ không riêng câu
    // liệt kê — chỗ này khác chip chốt hạ, thứ chỉ vô nghĩa ở câu liệt kê.
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

    // Ở câu LIỆT KÊ, xoá chip "khác" trần xong phần còn lại nguyên tử ⇒ vẫn lên chọn nhiều như thường.
    [Fact]
    public void Normalize_EnumerationQuestionWithABareOtherChip_DropsItAndKeepsMultiSelect()
    {
        var reply = _parser.Normalize(new BAChatReply
        {
            Message = "Record đi qua những trạng thái nào?",
            Suggestions = new List<string> { "Waiting Active", "Active", "Đã thu hồi", "Trạng thái khác" }
        });

        Assert.True(reply.MultiSelect);
        Assert.Equal(3, reply.Suggestions.Count);
        Assert.DoesNotContain("Trạng thái khác", reply.Suggestions);
    }

    // Chip của từng câu trong lượt gộp đi qua cùng ShapeAnswer — sót đường này là guard vắng mặt ở đúng
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
