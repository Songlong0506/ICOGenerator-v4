using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Trần TỔNG phần chữ của tài liệu nguồn, cộng dồn trên mọi nguồn. Trần mỗi file
// (Llm:SourceUpload:MaxTextCharsPerFile) không chặn được tổng: đủ nhiều file là phần nguồn một mình đã
// đẩy prompt qua vách giá. Điều phải khóa lại: cắt thì cắt, nhưng KHÔNG BAO GIỜ im lặng.
public class SourceContextTextBudgetTests
{
    // ContextWindow nhỏ ⇒ ngân sách chữ nhỏ, để test không phải dựng hàng trăm KB.
    // PromptBudget.Resolve(8.000) = max(4.000, 8.000-32.000) = 4.000 ⇒ SourceTokens = 1.333 token
    // ≈ 4.500 ký tự cho TOÀN BỘ phần chữ (theo mật độ của Filler bên dưới).
    private static AiModel SmallModel => new() { ModelId = "m", ContextWindow = 8_000, SupportsVision = false };

    /// <summary>
    /// Một câu tiếng Việt có dấu, lặp lại để lấp đầy. KHÔNG dùng <c>new string('x', n)</c>: BPE gộp
    /// 'xxxxxxxx' thành MỘT token, nên chuỗi lặp một ký tự ra 8 ký tự/token — rẻ hơn 2,4 lần văn bản thật
    /// (3,4 ký tự/token) và test sẽ đo ngân sách ở một mật độ mà production không bao giờ gặp.
    /// <c>Sentinel</c> chỉ xuất hiện trong phần lấp này, nên đếm nó là đếm đúng phần chữ nguồn đã sống sót
    /// qua ngân sách, không lẫn với khung bao quanh.
    /// </summary>
    private const string Sentinel = "Qxvn";
    private const string FillerUnit = "Quy tắc " + Sentinel + " về khóa học tự chọn của nhân viên phòng ban. ";

    private static string Filler(int chars) =>
        string.Concat(Enumerable.Repeat(FillerUnit, chars / FillerUnit.Length + 1))[..chars];

    private static int SentinelCount(string text) =>
        text.Split(Sentinel).Length - 1;

    private static SourceContextBuilder NewBuilder() =>
        new(new ConfigurationBuilder().Build(), NullLogger<SourceContextBuilder>.Instance);

    private static ProjectSourceFile TextFile(string name, int chars) => new()
    {
        Id = Guid.NewGuid(),
        Kind = SourceFileKind.Document,
        FileName = name,
        StoredPath = "/dev/null",
        ExtractedText = Filler(chars),
        CreatedAt = DateTime.UtcNow.AddSeconds(name.Length),
    };

    private static string TextOf(SourceContext ctx) =>
        string.Concat(ctx.Contents.OfType<TextContent>().Select(t => t.Text));

    [Fact]
    public void ManyFiles_AreCutToFitTheTotalBudget_NotJustThePerFileCap()
    {
        // Bốn file, mỗi file 2.000 ký tự (đều lọt trần mỗi-file) — nhưng tổng 8.000 ký tự ≈ 2.350 token,
        // vượt ngân sách 1.333 token.
        var sources = new[] { TextFile("a", 2_000), TextFile("b", 2_000), TextFile("c", 2_000), TextFile("d", 2_000) };

        var all = string.Concat(sources.Select(x => x.ExtractedText));
        var text = TextOf(NewBuilder().Build(sources, SmallModel));

        // Phần chữ nguồn phải bị kẹp lại quanh ngân sách, không phải đi đủ cả bốn file.
        Assert.True(SentinelCount(text) < SentinelCount(all),
            $"đã đi {SentinelCount(text)}/{SentinelCount(all)} đơn vị chữ — lẽ ra phải bị cắt");
    }

    // Cắt trong im lặng là mời BA hỏi lại người dùng đúng thứ họ đã upload, hoặc tệ hơn là tự bịa nốt phần
    // không thấy — cùng lý do mà câu ghi chú phần ảnh phải nói đúng số ảnh đi kèm.
    [Fact]
    public void WhenBudgetRunsOut_TheContextSaysSo_InsteadOfDroppingContentSilently()
    {
        var sources = new[] { TextFile("a", 20_000), TextFile("b", 20_000) };

        var text = TextOf(NewBuilder().Build(sources, SmallModel));

        Assert.Contains("hết hạn mức ngữ cảnh", text);
        Assert.Contains("TUYỆT ĐỐI không suy đoán", text);
    }

    // BẤT BIẾN QUAN TRỌNG NHẤT: ngân sách cạn KHÔNG được biến VisionSummary thành null. Điều kiện
    // "summary == null" là thứ quyết định có GỬI LẠI ẢNH hay không — một nguồn đã mô tả xong mà bị đọc
    // lại bằng ảnh thì đắt gấp bội đúng thứ trần này đang cố tiết kiệm.
    [Fact]
    public void ExhaustedBudget_NeverReSendsImagesForAnAlreadyDescribedSource()
    {
        var hog = TextFile("hog", 20_000);
        var described = TextFile("described", 20_000);
        described.Kind = SourceFileKind.Pdf;
        described.ScannedPageImageCount = 3;
        described.VisionSummary = new string('y', 5_000);

        var visionModel = new AiModel { ModelId = "m", ContextWindow = 8_000, SupportsVision = true };
        var built = NewBuilder().Build(new[] { hog, described }, visionModel);

        Assert.DoesNotContain(built.Contents, c => c is DataContent);
        Assert.Empty(built.FullyAttachedSourceIds);
    }
}
