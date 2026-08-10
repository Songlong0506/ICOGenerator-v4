using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// BẢNG CỘT là chỗ người dùng chốt "cột nào của app mới, cột nào là của hệ cũ". Cả tính năng đứng trên một
// tiền đề duy nhất: mọi dòng trong bảng phải là một cột CÓ THẬT trong file, gọi đúng cái tên mà hàng tiêu
// đề gọi. Model không giữ được tiền đề đó bằng thiện chí — nó viết hoa khác đi, nó bỏ sót cột nó thấy
// không thú vị, và thỉnh thoảng nó bịa hẳn một cột. Hai hậu quả đều câm lặng:
//
//   • dòng bịa/sai chính tả ⇒ người dùng tích một dòng vô nghĩa, rồi RealSampleDataReader đi lọc dữ liệu
//     mẫu theo cái tên không khớp cột nào;
//   • cột bị bỏ sót ⇒ nó biến mất khỏi bảng và mặc nhiên bị coi là "của hệ cũ" — đúng khiếm khuyết của
//     hàng chip mà bảng sinh ra để chữa.
//
// Lớp dưới đây là cái phanh cho cả hai, và các test này giữ nó.
public class SourceColumnMapBuilderTests
{
    private const string ExtractedText = """
        ### Sheet: Sheet1
        Tổng: 262 dòng dữ liệu, 4 cột.

        #### Thống kê cột (trên 262 dòng)
        - Global ID: có giá trị 262/262 · 13 giá trị phân biệt · hay gặp nhất: 10151719 (60)
        - Item Title: có giá trị 257/262 · 136 giá trị phân biệt
        - Assignment Type: có giá trị 136/262 · ĐỦ 3 giá trị: REQ (78), MAN (53), OPT (5)
        - Revision Number: có giá trị 262/262 · ĐỦ 3 giá trị: 1 (218), 3 (21), 2 (18)

        #### Dòng dữ liệu (2 dòng đầu làm mẫu)
        Global ID | Item Title | Assignment Type | Revision Number
        11054396 | Quality at Bosch-B | REQ | 1
        """;

    [Fact]
    public void ReadColumnNames_TakesTheHeaderNamesFromTheColumnStatsBlock()
    {
        var columns = SourceColumnMapBuilder.ReadColumnNames(ExtractedText);

        Assert.Equal(new[] { "Global ID", "Item Title", "Assignment Type", "Revision Number" }, columns);
    }

    // Bảng nhiều sheet có nhiều khối thống kê; bảng cột phủ cả file nên gộp lại, bỏ trùng.
    [Fact]
    public void ReadColumnNames_MergesEverySheet_WithoutDuplicates()
    {
        var text = ExtractedText + """


            ### Sheet: DanhMuc
            Tổng: 9 dòng dữ liệu, 2 cột.

            #### Thống kê cột (trên 9 dòng)
            - Global ID: có giá trị 9/9 · 9 giá trị phân biệt
            - Room: có giá trị 9/9 · ĐỦ 2 giá trị: A1 (5), A2 (4)
            """;

        var columns = SourceColumnMapBuilder.ReadColumnNames(text);

        Assert.Contains("Room", columns);
        Assert.Single(columns, c => c == "Global ID");
    }

    // Text không phải bảng tính (Word, PDF) không có khối thống kê ⇒ không có bảng cột nào được dựng, và
    // luồng chạy đúng như trước khi có tính năng này.
    [Fact]
    public void ReadColumnNames_ReturnsEmpty_WhenThereIsNoColumnStatsBlock()
    {
        Assert.Empty(SourceColumnMapBuilder.ReadColumnNames("Quy trình duyệt đơn gồm ba bước."));
        Assert.Empty(SourceColumnMapBuilder.ReadColumnNames(null));
    }

    [Fact]
    public void Build_KeepsProposals_ButAlwaysCoversEveryColumnOfTheFile()
    {
        var proposed = new List<SourceColumnNote>
        {
            new() { FileName = "plan.xlsx", Column = "Global ID", Meaning = "mã số nhân viên", Used = true },
            new() { FileName = "plan.xlsx", Column = "Revision Number", Meaning = "phiên bản nội dung", Used = false }
        };

        var rows = SourceColumnMapBuilder.Build("plan.xlsx", proposed, SourceColumnMapBuilder.ReadColumnNames(ExtractedText));

        // Thứ tự theo FILE: người dùng đang đối chiếu với chính bảng tính của họ.
        Assert.Equal(new[] { "Global ID", "Item Title", "Assignment Type", "Revision Number" }, rows.Select(r => r.Column));

        // Cột model không nhắc tới vẫn có mặt — chưa tích, ý nghĩa trống — thay vì biến mất trong im lặng.
        var itemTitle = rows.Single(r => r.Column == "Item Title");
        Assert.False(itemTitle.Used);
        Assert.Equal("", itemTitle.Meaning);

        Assert.True(rows.Single(r => r.Column == "Global ID").Used);
        Assert.Equal("mã số nhân viên", rows.Single(r => r.Column == "Global ID").Meaning);
    }

    // Tên cột trong bảng luôn là chính tả của FILE. Người dùng đang đối chiếu từng dòng với bảng tính đang
    // mở; một dòng ghi "global id" trong khi Excel ghi "Global ID" là dòng họ phải dừng lại để nghĩ.
    [Fact]
    public void Build_UsesTheSpellingFromTheFile_NotFromTheModel()
    {
        var proposed = new List<SourceColumnNote>
        {
            new() { FileName = "plan.xlsx", Column = "global id", Meaning = "mã nhân viên", Used = true }
        };

        var rows = SourceColumnMapBuilder.Build("plan.xlsx", proposed, SourceColumnMapBuilder.ReadColumnNames(ExtractedText));

        Assert.Equal("Global ID", rows.Single(r => r.Used).Column);
    }

    [Fact]
    public void Build_DropsColumnsThatDoNotExistInTheFile()
    {
        var proposed = new List<SourceColumnNote>
        {
            new() { FileName = "plan.xlsx", Column = "Global ID", Meaning = "mã nhân viên", Used = true },
            new() { FileName = "plan.xlsx", Column = "Room Capacity", Meaning = "sức chứa phòng", Used = true }
        };

        var rows = SourceColumnMapBuilder.Build("plan.xlsx", proposed, SourceColumnMapBuilder.ReadColumnNames(ExtractedText));

        Assert.DoesNotContain(rows, r => r.Column == "Room Capacity");
        Assert.Equal(4, rows.Count);
    }

    // Một lượt upload có thể gửi nhiều bảng tính, và hai file hoàn toàn có thể có cột trùng tên. Dòng của
    // file kia phải nằm ở bảng của file kia, nếu không người dùng tích một cột tưởng của file này.
    [Fact]
    public void Build_IgnoresProposalsAddressedToAnotherFile()
    {
        var proposed = new List<SourceColumnNote>
        {
            new() { FileName = "rooms.xlsx", Column = "Global ID", Meaning = "mã phòng", Used = true },
            new() { FileName = "plan.xlsx", Column = "Item Title", Meaning = "tên lớp học", Used = true }
        };

        var rows = SourceColumnMapBuilder.Build("plan.xlsx", proposed, SourceColumnMapBuilder.ReadColumnNames(ExtractedText));

        Assert.False(rows.Single(r => r.Column == "Global ID").Used);
        Assert.True(rows.Single(r => r.Column == "Item Title").Used);
    }

    // Không đề xuất nào khớp ⇒ KHÔNG dựng bảng. Một bảng 18 dòng chưa tích, ý nghĩa trống sạch không phải
    // là "để người dùng tự điền" mà là lượt hỏng: nó nói với họ rằng BA chưa đọc được file.
    [Fact]
    public void Build_ReturnsNothing_WhenTheModelProposedNothingUsable()
    {
        Assert.Empty(SourceColumnMapBuilder.Build("plan.xlsx", new List<SourceColumnNote>(), SourceColumnMapBuilder.ReadColumnNames(ExtractedText)));
        Assert.Empty(SourceColumnMapBuilder.Build("plan.xlsx", null, SourceColumnMapBuilder.ReadColumnNames(ExtractedText)));
        Assert.Empty(SourceColumnMapBuilder.Build("plan.xlsx",
            new List<SourceColumnNote> { new() { Column = "Không có thật", Used = true } },
            SourceColumnMapBuilder.ReadColumnNames(ExtractedText)));
    }

    [Fact]
    public void RenderConfirmedBlock_TellsBothHalves_WhatIsUsedAndWhatIsLegacy()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new List<SourceColumnNote>
        {
            new() { FileName = "plan.xlsx", Column = "Global ID", Meaning = "mã số nhân viên", Used = true },
            new() { FileName = "plan.xlsx", Column = "Revision Number", Meaning = "phiên bản nội dung", Used = false }
        });

        var block = SourceColumnMapBuilder.RenderConfirmedBlock("plan.xlsx", json);

        Assert.NotNull(block);
        Assert.Contains("đã được NGƯỜI DÙNG CHỐT", block!, StringComparison.Ordinal);
        Assert.Contains("- Global ID: mã số nhân viên", block, StringComparison.Ordinal);
        // Vế "không dùng" phải được gọi tên: đó là thứ chặn BA hỏi lại và chặn tài liệu dựng trên cột hệ cũ.
        Assert.Contains("Revision Number", block, StringComparison.Ordinal);
        Assert.Contains("Cột KHÔNG dùng", block, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderConfirmedBlock_ReturnsNull_WhenNothingConfirmedYet()
    {
        Assert.Null(SourceColumnMapBuilder.RenderConfirmedBlock("plan.xlsx", null));
        Assert.Null(SourceColumnMapBuilder.RenderConfirmedBlock("plan.xlsx", "{ hỏng"));
    }

    [Fact]
    public void UsedColumns_ReturnsOnlyTheTickedOnes()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new List<SourceColumnNote>
        {
            new() { Column = "Global ID", Used = true },
            new() { Column = "Item Title", Used = true },
            new() { Column = "Revision Number", Used = false }
        });

        Assert.Equal(new[] { "Global ID", "Item Title" }, SourceColumnMapBuilder.UsedColumns(json));
    }
}
