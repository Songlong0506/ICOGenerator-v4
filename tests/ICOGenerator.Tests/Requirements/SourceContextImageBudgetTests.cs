using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Trần TOKEN của phần ảnh, cộng dồn trên mọi nguồn của một lời gọi. Hai trần cũ không thay được nó: trần SỐ
// ảnh coi một icon 32px bằng một ảnh chụp màn hình full-width (chênh nhau ~6 lần token), còn trần DUNG LƯỢNG
// đo cỡ gói tin — cùng khung hình, PNG và JPEG nén mạnh chênh nhau hàng chục lần byte mà token y hệt nhau.
// Điều phải khóa lại: cắt vì hết token thì cắt, nhưng câu ghi chú vẫn phải nói ĐÚNG số ảnh đã đi.
public class SourceContextImageBudgetTests : IDisposable
{
    // PromptBudget.Resolve(30.000) = (30.000/2) * 5/8 = 9.375 ⇒ ImageTokens = 1.562 token.
    // Một ảnh 1024×1024 là 765 token ⇒ lọt đúng HAI ảnh, ảnh thứ ba vượt trần.
    private static AiModel VisionModel => new() { ModelId = "m", ContextWindow = 30_000, SupportsVision = true };

    // Trần nhỏ tới mức không ảnh nào lọt: ImageTokens = 2.500/6 = 416 < 765.
    private static AiModel TinyVisionModel => new() { ModelId = "m", ContextWindow = 8_000, SupportsVision = true };

    private readonly string _dir;

    public SourceContextImageBudgetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ico-imagebudget-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ImagesBeyondTheTokenBudget_AreLeftOut_EvenThoughTheImageCountCapIsNowhereNear()
    {
        var pdf = ScannedPdf(pages: 3, width: 1024, height: 1024);

        var built = NewBuilder().Build(new[] { pdf }, VisionModel);

        // Trần số ảnh mặc định là 12 — không phải thứ chặn ở đây.
        Assert.Equal(2, built.Contents.OfType<DataContent>().Count());
    }

    // Cắt trong im lặng là mời model bịa nội dung phần hình không thấy — cùng nguyên tắc với phần chữ.
    [Fact]
    public void TheNote_TellsTheTruth_AboutHowManyImagesActuallyWent()
    {
        var pdf = ScannedPdf(pages: 3, width: 1024, height: 1024);

        var text = TextOf(NewBuilder().Build(new[] { pdf }, VisionModel));

        Assert.Contains("kèm 2/3 hình", text);
        Assert.Contains("TUYỆT ĐỐI", text);
    }

    // Nguồn bị cắt KHÔNG được coi là "đã gửi đủ ảnh": chốt lại thành VisionSummary lúc này là mất trắng
    // phần hình chưa bao giờ tới được model.
    [Fact]
    public void ASourceCutByTheTokenBudget_IsNotLockedIntoAVisionSummary()
    {
        var pdf = ScannedPdf(pages: 3, width: 1024, height: 1024);

        Assert.Empty(NewBuilder().Build(new[] { pdf }, VisionModel).FullyAttachedSourceIds);
    }

    [Fact]
    public void WhenNotEvenOneImageFits_TheNoteSaysNoneWentAtAll()
    {
        var pdf = ScannedPdf(pages: 2, width: 1024, height: 1024);

        var built = NewBuilder().Build(new[] { pdf }, TinyVisionModel);

        Assert.DoesNotContain(built.Contents, c => c is DataContent);
        Assert.Contains("hết hạn mức ảnh", TextOf(built));
    }

    // Trần đo TOKEN chứ không đo số ảnh: cùng ngân sách đó, một tá icon vẫn đi trọn vẹn trong khi ba ảnh
    // chụp màn hình thì không. Đây là điều một trần "số ảnh" không bao giờ làm được.
    [Fact]
    public void SmallIcons_AllFit_WhereScreenshotsWouldNot()
    {
        var pdf = ScannedPdf(pages: 6, width: 64, height: 64);

        var built = NewBuilder().Build(new[] { pdf }, VisionModel);

        Assert.Equal(6, built.Contents.OfType<DataContent>().Count());
        Assert.Single(built.FullyAttachedSourceIds);
    }

    private static SourceContextBuilder NewBuilder() =>
        new(new ConfigurationBuilder().Build(), NullLogger<SourceContextBuilder>.Instance);

    private static string TextOf(SourceContext ctx) =>
        string.Concat(ctx.Contents.OfType<TextContent>().Select(t => t.Text));

    // Một PDF scan với N ảnh trang trên đĩa (page-{n}.png, đúng quy ước PdfScanPageRenderer).
    private ProjectSourceFile ScannedPdf(int pages, int width, int height)
    {
        var stored = Path.Combine(_dir, "tai-lieu.pdf");
        File.WriteAllBytes(stored, new byte[] { 0x25, 0x50, 0x44, 0x46 });
        for (var n = 1; n <= pages; n++)
            File.WriteAllBytes(Path.Combine(_dir, $"page-{n}.png"), Png(width, height));

        return new ProjectSourceFile
        {
            Id = Guid.NewGuid(),
            Kind = SourceFileKind.Pdf,
            FileName = "tai-lieu.pdf",
            StoredPath = stored,
            ScannedPageImageCount = pages,
            CreatedAt = DateTime.UtcNow,
        };
    }

    // Chỉ cần header: bộ ước lượng đọc kích thước từ IHDR, không giải mã pixel nào.
    private static byte[] Png(int width, int height)
    {
        var b = new byte[24];
        b[0] = 0x89; b[1] = (byte)'P'; b[2] = (byte)'N'; b[3] = (byte)'G';
        b[4] = 0x0D; b[5] = 0x0A; b[6] = 0x1A; b[7] = 0x0A;
        b[11] = 13;
        b[12] = (byte)'I'; b[13] = (byte)'H'; b[14] = (byte)'D'; b[15] = (byte)'R';
        WriteBe32(b, 16, width);
        WriteBe32(b, 20, height);
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
