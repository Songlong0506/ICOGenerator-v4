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
// thuẫn — được chắt thẳng vào bản đồ bao phủ và "Điều đã chốt" như lời người dùng, nơi không tầng nào
// phía sau (Product Brief, spec, POC) còn phân biệt được nữa.
//
// Ba bất biến giữ chỗ này:
//  1. Chip mang dấu hiệu "phương án" (gói nhiều thứ, loại trừ, không tự đứng một mình) ⇒ multiSelect bị
//     HẠ về false. Prompt dạy cách viết chip nguyên tử; parser là cái phanh khi prompt bị trượt.
//  2. Sửa CHỈ MỘT CHIỀU — không bao giờ tự bật multiSelect. Hạ nhầm thì người dùng mất tiện ích tích
//     nhiều ô (vẫn bấm được một chip, vẫn tự nhập); bật nhầm thì sinh ra dữ liệu sai. Không cùng hạng giá.
//  3. Áp ở CẢ hai đường vào (Parse cho model trả text, Normalize cho structured output) và cho CẢ chip
//     lượt-đơn lẫn chip của từng câu trong lượt gộp — sót một đường là guard vắng mặt đúng chỗ nó cần.
public class BAChatSuggestionShapeTests
{
    private readonly BAChatReplyParser _parser = new();

    private static BAChatReply Single(bool multiSelect, params string[] suggestions) => new()
    {
        Message = "Ứng dụng sẽ có những vai trò nào?",
        Suggestions = suggestions.ToList(),
        MultiSelect = multiSelect
    };

    // Chính ca đã gặp trên màn hình.
    [Fact]
    public void Normalize_NestedRoleBundlesWithMultiSelect_IsDowngradedToSingleSelect()
    {
        var reply = _parser.Normalize(Single(true,
            "Nhân viên và HR/đào tạo",
            "Nhân viên, quản lý và HR",
            "Thêm HoD phòng ban",
            "Chỉ bộ phận HR/đào tạo"));

        Assert.False(reply.MultiSelect);
        // Chip vẫn còn nguyên: guard chỉ đổi CÁCH trả lời, không bao giờ nuốt mất phương án của BA.
        Assert.Equal(4, reply.Suggestions.Count);
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

    [Theory]
    // (1) chip LOẠI TRỪ: tự nó bao hàm hoặc phủ định phần còn lại.
    [InlineData("Chỉ bộ phận HR")]
    [InlineData("Tất cả nhân viên nhà máy")]
    [InlineData("Không cần thông báo cho ai")]
    // (2) chip KHÔNG TỰ ĐỨNG: chỉ có nghĩa khi đọc kèm chip khác.
    [InlineData("Thêm HoD phòng ban")]
    [InlineData("Cả hai bên trên")]
    // (3) chip GÓI: nêu từ hai thứ trở lên trong một dòng.
    [InlineData("Nhân viên và HoD")]
    [InlineData("Nhân viên, HR")]
    public void Normalize_AnySingleOffendingChip_DowngradesTheWholeSet(string offending)
    {
        var reply = _parser.Normalize(Single(true, "Nhân viên", "HR – Đào tạo", offending));

        Assert.False(reply.MultiSelect);
    }

    // Dấu "/" thường là một cái TÊN ("HR/đào tạo", "TEF3.3/LL06"), không phải liệt kê hai thứ.
    [Fact]
    public void Normalize_SlashInsideAChipName_IsNotTreatedAsABundle()
    {
        var reply = _parser.Normalize(Single(true, "HR/đào tạo", "Nhân viên", "HoD phòng ban"));

        Assert.True(reply.MultiSelect);
    }

    // Guard chỉ HẠ, không bao giờ NÂNG: BA để multiSelect=false có thể là cố ý (muốn người dùng chọn ra
    // đúng một phương án quan trọng nhất). Tự bật lên là parser tự quyết thay BA.
    [Fact]
    public void Normalize_AtomicChipsWithoutTheFlag_StaySingleSelect()
    {
        var reply = _parser.Normalize(Single(false, "Nhân viên", "Manager orgUnit", "HoD phòng ban"));

        Assert.False(reply.MultiSelect);
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
}
