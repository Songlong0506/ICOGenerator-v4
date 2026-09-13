using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Ảnh gửi kèm prompt từng được tính ĐÚNG 0 token: bộ ước lượng chỉ nhận chuỗi, và ChatMessage.Text bỏ qua
// mọi DataContent. Hệ quả: lượt tốn nhất của cả app (12 ảnh chụp màn hình) được ghi sổ y như lượt không có
// ảnh nào — sai cả trần prompt lẫn chi phí ở mọi endpoint không trả usage. Các test này khóa hai nửa của
// lời giải: công thức chia ô, và việc đọc được kích thước từ header của các định dạng ảnh thật.
public class ImageTokenEstimateTests
{
    // Các mốc của bảng giá vision: ảnh thu về vừa 2048px, cạnh ngắn về 768px, chia ô 512px, 85 + 170/ô.
    [Theory]
    [InlineData(1024, 1024, 765)]   // → 768×768 = 4 ô
    [InlineData(1024, 1536, 1105)]  // → 768×1152 = 6 ô
    [InlineData(4096, 1536, 1445)]  // → 2048×768 = 8 ô (ảnh chụp màn hình full-width, mức đắt nhất)
    [InlineData(512, 512, 255)]     // cạnh ngắn đã dưới 768 ⇒ giữ nguyên, 1 ô
    public void TileFormula_MatchesTheProviderPriceTable(int width, int height, int expected)
    {
        Assert.Equal(expected, TokenEstimator.EstimateImage(width, height));
    }

    // Chuẩn hóa chỉ được phép THU NHỎ. Phóng một icon 32px lên 768px là bắt nó trả tiền như ảnh chụp màn
    // hình — sai chiều đúng ở loại ảnh nhiều nhất trong tài liệu Word (logo, con dấu, icon).
    [Fact]
    public void TinyIcon_IsNotChargedLikeAScreenshot()
    {
        Assert.Equal(255, TokenEstimator.EstimateImage(32, 32));
        Assert.True(TokenEstimator.EstimateImage(32, 32) < TokenEstimator.EstimateImage(1024, 1024));
    }

    // Hằng số đường lùi phải bám công thức, không được trôi khi công thức đổi.
    [Fact]
    public void UnknownImageTokens_EqualsASquare1024Image()
    {
        Assert.Equal(TokenEstimator.UnknownImageTokens, TokenEstimator.EstimateImage(1024, 1024));
    }

    [Fact]
    public void PngHeader_GivesTheRealDimensions()
    {
        Assert.Equal(1105, TokenEstimator.EstimateImage(Png(1024, 1536)));
        Assert.Equal(TokenEstimator.EstimateImage(1600, 1200), TokenEstimator.EstimateImage(Png(1600, 1200)));
    }

    // JPEG không có offset cố định — kích thước nằm sau một số khung metadata tùy máy chụp, nên bộ đọc phải
    // đi lần theo chuỗi khung chứ không nhảy tới một vị trí đoán trước.
    [Fact]
    public void JpegHeader_IsFound_AfterMetadataSegments()
    {
        Assert.Equal(TokenEstimator.EstimateImage(1024, 1536), TokenEstimator.EstimateImage(Jpeg(1024, 1536)));
    }

    [Fact]
    public void GifHeader_GivesTheRealDimensions()
    {
        Assert.Equal(TokenEstimator.EstimateImage(800, 600), TokenEstimator.EstimateImage(Gif(800, 600)));
    }

    // Định dạng lạ / header cụt KHÔNG được rơi về 0: 0 chính là cái lỗi cơ chế này sinh ra để sửa.
    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' })] // chữ ký PNG nhưng cụt mất IHDR
    public void UnreadableBytes_StillCostSomething(byte[] bytes)
    {
        Assert.Equal(TokenEstimator.UnknownImageTokens, TokenEstimator.EstimateImage(bytes));
    }

    // Phần CHỮ không được đổi hành vi: mọi con số cũ (trần hội thoại, trần text nguồn) suy từ đúng nó.
    [Fact]
    public void TextEstimate_IsUnchanged()
    {
        Assert.Equal(0, TokenEstimator.Estimate(null));
        Assert.Equal(1, TokenEstimator.Estimate("abcd"));
        Assert.Equal(250, TokenEstimator.Estimate(new string('x', 1000)));
    }

    // ── Header tối thiểu của từng định dạng: chỉ cần đủ để bộ đọc lấy ra kích thước ───────────────────

    private static byte[] Png(int width, int height)
    {
        var b = new byte[24];
        b[0] = 0x89; b[1] = (byte)'P'; b[2] = (byte)'N'; b[3] = (byte)'G';
        b[4] = 0x0D; b[5] = 0x0A; b[6] = 0x1A; b[7] = 0x0A;
        b[11] = 13; // độ dài chunk IHDR
        b[12] = (byte)'I'; b[13] = (byte)'H'; b[14] = (byte)'D'; b[15] = (byte)'R';
        WriteBe32(b, 16, width);
        WriteBe32(b, 20, height);
        return b;
    }

    private static byte[] Jpeg(int width, int height)
    {
        var b = new List<byte> { 0xFF, 0xD8 };
        // Một khung APP0 (JFIF) đứng chắn phía trước để test đi được qua metadata.
        b.AddRange(new byte[] { 0xFF, 0xE0, 0x00, 0x10 });
        b.AddRange(new byte[14]); // 0x10 = 16 tính CẢ hai byte độ dài ⇒ thân còn 14 byte
        // SOF0: độ dài, độ sâu màu, rồi CAO trước RỘNG.
        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });
        b.AddRange(new[] { (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width });
        b.AddRange(new byte[8]);
        return b.ToArray();
    }

    private static byte[] Gif(int width, int height)
    {
        var b = new byte[13];
        "GIF89a"u8.CopyTo(b);
        b[6] = (byte)width; b[7] = (byte)(width >> 8);
        b[8] = (byte)height; b[9] = (byte)(height >> 8);
        return b;
    }

    private static void WriteBe32(byte[] b, int i, int value)
    {
        b[i] = (byte)(value >> 24);
        b[i + 1] = (byte)(value >> 16);
        b[i + 2] = (byte)(value >> 8);
        b[i + 3] = (byte)value;
    }
}
