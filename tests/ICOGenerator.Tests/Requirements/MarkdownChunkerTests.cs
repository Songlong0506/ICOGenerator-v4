using ICOGenerator.Services.Requirements.Knowledge;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Chunker cắt tài liệu theo heading markdown, gói paragraph tới trần ký tự và bỏ vụn quá ngắn —
// các bất biến mà chất lượng truy xuất (không lẫn mục, không đoạn khổng lồ) phụ thuộc vào.
public class MarkdownChunkerTests
{
    [Fact]
    public void Split_SectionsByHeading_EachChunkKeepsItsHeading()
    {
        var content = """
            # Phạm vi
            Ứng dụng quản lý kho vật tư cho phân xưởng, theo dõi nhập xuất tồn theo thời gian thực hằng ngày.

            # Luồng duyệt
            Phiếu xuất kho phải được manager của orgUnit duyệt trước khi thủ kho xuất hàng khỏi kho chính.
            """;

        var chunks = MarkdownChunker.Split(content);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Phạm vi", chunks[0].Heading);
        Assert.Contains("quản lý kho vật tư", chunks[0].Text);
        Assert.Equal("Luồng duyệt", chunks[1].Heading);
        Assert.Contains("manager của orgUnit duyệt", chunks[1].Text);
    }

    [Fact]
    public void Split_ContentBeforeFirstHeading_BecomesHeadinglessChunk()
    {
        var content = "Đoạn mở đầu mô tả tổng quan bài toán quản lý kho vật tư của phân xưởng sản xuất.\n\n# Mục A\nNội dung mục A đủ dài để vượt ngưỡng tối thiểu của một đoạn truy xuất hợp lệ.";

        var chunks = MarkdownChunker.Split(content);

        Assert.Equal(2, chunks.Count);
        Assert.Null(chunks[0].Heading);
        Assert.Equal("Mục A", chunks[1].Heading);
    }

    [Fact]
    public void Split_LongSection_IsPackedIntoChunksUnderMax()
    {
        // 12 paragraph ~200 ký tự ⇒ một section ~2400 ký tự phải bị cắt thành nhiều đoạn dưới trần.
        var paragraph = string.Concat(Enumerable.Repeat("nội dung nghiệp vụ ", 11)).Trim(); // ~200 chars
        var content = "# Mục dài\n" + string.Join("\n\n", Enumerable.Repeat(paragraph, 12));

        var chunks = MarkdownChunker.Split(content);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c =>
        {
            Assert.Equal("Mục dài", c.Heading);
            Assert.True(c.Text.Length <= MarkdownChunker.MaxChunkChars);
        });
    }

    [Fact]
    public void Split_OversizedSingleParagraph_IsHardSplit()
    {
        var content = "# Bảng\n" + new string('x', MarkdownChunker.MaxChunkChars * 2 + 100);

        var chunks = MarkdownChunker.Split(content);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= MarkdownChunker.MaxChunkChars));
    }

    [Fact]
    public void Split_TinyFragmentsAndEmptyContent_AreDropped()
    {
        Assert.Empty(MarkdownChunker.Split(""));
        Assert.Empty(MarkdownChunker.Split("   \n\n  "));
        // Section chỉ có một dòng ngắn dưới ngưỡng tối thiểu ⇒ bị bỏ.
        Assert.Empty(MarkdownChunker.Split("# Heading trơ\nngắn quá"));
    }
}
