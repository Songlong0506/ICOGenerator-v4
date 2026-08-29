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
    // PromptBudget.Resolve(8.000) = max(4.000, 8.000-32.000) * 5/8 = 2.500 ⇒ SourceTokens = 833 token
    // ≈ 3.332 ký tự cho TOÀN BỘ phần chữ.
    private static AiModel SmallModel => new() { ModelId = "m", ContextWindow = 8_000, SupportsVision = false };

    private static SourceContextBuilder NewBuilder() =>
        new(new ConfigurationBuilder().Build(), NullLogger<SourceContextBuilder>.Instance);

    private static ProjectSourceFile TextFile(string name, int chars) => new()
    {
        Id = Guid.NewGuid(),
        Kind = SourceFileKind.Document,
        FileName = name,
        StoredPath = "/dev/null",
        ExtractedText = new string('x', chars),
        CreatedAt = DateTime.UtcNow.AddSeconds(name.Length),
    };

    private static string TextOf(SourceContext ctx) =>
        string.Concat(ctx.Contents.OfType<TextContent>().Select(t => t.Text));

    [Fact]
    public void ManyFiles_AreCutToFitTheTotalBudget_NotJustThePerFileCap()
    {
        // Bốn file, mỗi file 2.000 ký tự (đều lọt trần mỗi-file) — nhưng tổng 8.000 vượt ngân sách ~3.332.
        var sources = new[] { TextFile("a", 2_000), TextFile("b", 2_000), TextFile("c", 2_000), TextFile("d", 2_000) };

        var text = TextOf(NewBuilder().Build(sources, SmallModel));

        // Phần chữ thật (các ký tự 'x') phải bị kẹp lại quanh ngân sách, không phải đủ cả 8.000.
        Assert.True(text.Count(c => c == 'x') < 5_000);
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
