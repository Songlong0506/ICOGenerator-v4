using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Biến các <see cref="ProjectSourceFile"/> của một project thành danh sách <see cref="AIContent"/> để gắn kèm
/// lượt user khi gọi LLM: <see cref="TextContent"/> cho text đã bóc, <see cref="DataContent"/> cho phần ảnh —
/// ảnh người dùng upload trực tiếp, ảnh trang PDF scan (page-{n}.png) và hình nhúng bóc từ Word (figure-{n}.*).
/// Phần ảnh CHỈ được thêm khi model hỗ trợ vision; model text-only chỉ nhận text. Áp trần số ảnh + tổng dung
/// lượng ảnh ngay tại đây để chặn đốt token ngoài kiểm soát.
/// </summary>
public class SourceContextBuilder
{
    private readonly ILogger<SourceContextBuilder> _logger;
    private readonly int _maxImages;
    private readonly long _maxTotalImageBytes;
    private readonly int _maxTextCharsPerFile;

    public SourceContextBuilder(IConfiguration configuration, ILogger<SourceContextBuilder> logger)
    {
        _logger = logger;
        _maxImages = configuration.GetValue("Llm:SourceUpload:MaxImagesPerCall", 6);
        _maxTotalImageBytes = configuration.GetValue("Llm:SourceUpload:MaxTotalImageBytes", 20L * 1024 * 1024);
        _maxTextCharsPerFile = configuration.GetValue("Llm:SourceUpload:MaxTextCharsPerFile", 20000);
    }

    /// <summary>Trả về danh sách rỗng nếu không có nguồn (caller giữ nguyên message text thuần như cũ).</summary>
    public List<AIContent> Build(IEnumerable<ProjectSourceFile>? sources, bool modelSupportsVision)
    {
        var contents = new List<AIContent>();
        var list = sources?.OrderBy(s => s.CreatedAt).ToList() ?? new List<ProjectSourceFile>();
        if (list.Count == 0)
            return contents;

        contents.Add(new TextContent(
            "\n\n=== TÀI LIỆU NGUỒN DO NGƯỜI DÙNG CUNG CẤP (tham khảo khi phân tích yêu cầu) ==="));

        var imageCount = 0;
        long imageBytes = 0;

        foreach (var s in list)
        {
            var header = $"\n[Nguồn: {s.FileName}]";
            if (!string.IsNullOrWhiteSpace(s.ExtractedText))
            {
                var text = s.ExtractedText!.Length > _maxTextCharsPerFile
                    ? s.ExtractedText[.._maxTextCharsPerFile] + "\n…(đã cắt bớt)"
                    : s.ExtractedText;
                // Nguồn vừa có text vừa có ảnh (Word có hình nhúng, PDF nửa chữ nửa scan): nói rõ ảnh có được
                // gửi kèm hay không, cùng lý do chống-bịa như nhánh dưới.
                var imageNote = s.ScannedPageImageCount > 0
                    ? (modelSupportsVision
                        ? $" (kèm {s.ScannedPageImageCount} hình trích từ tài liệu — xem ảnh đính kèm, đối chiếu các mốc [Hình n] trong text nếu có)"
                        : " (tài liệu có hình nhưng model hiện tại KHÔNG đọc được ảnh nên hình KHÔNG được gửi kèm; TUYỆT ĐỐI không tự suy đoán nội dung hình)")
                    : string.Empty;
                contents.Add(new TextContent(header + imageNote + "\n" + text));
            }
            else
            {
                contents.Add(new TextContent(header + (s.Kind switch
                {
                    // Ảnh CHỈ đọc được khi model có vision. Với model text-only, KHÔNG được viết "xem nội dung ảnh
                    // đính kèm" (ảnh không hề được gửi kèm ở dưới) — câu đó khiến model tưởng có ảnh và BỊA nội
                    // dung. Nói thẳng là không đọc được để BA hỏi người dùng gõ lại thay vì tự suy đoán.
                    SourceFileKind.Image => modelSupportsVision
                        ? " (ảnh — xem nội dung ảnh đính kèm)"
                        : " (ảnh — model hiện tại KHÔNG đọc được ảnh nên nội dung ảnh KHÔNG được gửi kèm; TUYỆT ĐỐI không tự suy đoán nội dung ảnh, hãy hỏi người dùng gõ/nhập lại các thông tin trong ảnh)",
                    SourceFileKind.Spreadsheet => " (bảng tính — không đọc được nội dung, đã bỏ qua)",
                    // Word không có chữ nào (vd toàn ảnh scan dán vào) nhưng lấy được hình ⇒ nội dung ĐI KÈM
                    // dưới dạng ảnh khi model có vision — đừng nói "bị bỏ qua".
                    SourceFileKind.Document => s.ScannedPageImageCount > 0
                        ? (modelSupportsVision
                            ? $" (tài liệu Word — không có text, {s.ScannedPageImageCount} hình trong tài liệu được gửi kèm dưới dạng ẢNH, xem nội dung ảnh đính kèm)"
                            : " (tài liệu Word chỉ có hình — model hiện tại KHÔNG đọc được ảnh nên nội dung KHÔNG được gửi kèm; TUYỆT ĐỐI không tự suy đoán, hãy hỏi người dùng nhập lại các thông tin trong tài liệu)")
                        : " (tài liệu Word — không đọc được nội dung, đã bỏ qua)",
                    // PDF scan: có ảnh trang lấy được thì nội dung ĐI KÈM dưới dạng ảnh (khi model có
                    // vision), nên đừng nói "bị bỏ qua" — câu đó khiến BA đi hỏi lại thứ nó đang cầm.
                    _ => s.ScannedPageImageCount > 0
                        ? (modelSupportsVision
                            ? $" (PDF dạng scan — {s.ScannedPageImageCount} trang được gửi kèm dưới dạng ẢNH, xem nội dung ảnh đính kèm)"
                            : " (PDF dạng scan — model hiện tại KHÔNG đọc được ảnh nên nội dung KHÔNG được gửi kèm; TUYỆT ĐỐI không tự suy đoán, hãy hỏi người dùng nhập lại các thông tin trong tài liệu)")
                        : " (PDF dạng scan/ảnh — không trích xuất được text, nội dung bị bỏ qua)"
                })));
            }

            if (!modelSupportsVision)
                continue;

            foreach (var (path, mediaType) in EnumerateImageAssets(s))
            {
                if (imageCount >= _maxImages)
                    break;
                try
                {
                    if (!File.Exists(path))
                        continue;
                    var bytes = File.ReadAllBytes(path);
                    if (imageBytes + bytes.Length > _maxTotalImageBytes)
                        continue;
                    contents.Add(new DataContent(bytes, mediaType));
                    imageCount++;
                    imageBytes += bytes.Length;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Đọc ảnh nguồn {Path} thất bại; bỏ qua.", path);
                }
            }
        }

        return contents;
    }

    // Nguồn vision gồm: ảnh user upload trực tiếp, ảnh trang của PDF scan (PdfScanPageRenderer ghi
    // page-{n}.png cạnh file gốc), VÀ hình nhúng bóc từ Word (WordDocumentTextExtractor ghi figure-{n}.*).
    // Ảnh xếp theo SỐ THỨ TỰ chứ không theo thứ tự chuỗi, để trang/hình 10 không nhảy lên trước 2 —
    // model đọc một biểu mẫu nhiều trang cần đúng trình tự.
    private static IEnumerable<(string Path, string MediaType)> EnumerateImageAssets(ProjectSourceFile s)
    {
        if (s.Kind == SourceFileKind.Image)
        {
            yield return (s.StoredPath, string.IsNullOrWhiteSpace(s.ContentType) ? "image/png" : s.ContentType);
            yield break;
        }

        if (s.Kind is not (SourceFileKind.Pdf or SourceFileKind.Document) || s.ScannedPageImageCount <= 0)
            yield break;

        var dir = Path.GetDirectoryName(s.StoredPath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            yield break;

        if (s.Kind == SourceFileKind.Pdf)
        {
            var pages = Directory
                .EnumerateFiles(dir, PdfScanPageRenderer.PageImagePrefix + "*.png")
                .Select(path => (Path: path, Page: ParseAssetNumber(path, PdfScanPageRenderer.PageImagePrefix)))
                .Where(x => x.Page > 0)
                .OrderBy(x => x.Page);

            foreach (var page in pages)
                yield return (page.Path, "image/png");
            yield break;
        }

        var figures = Directory
            .EnumerateFiles(dir, WordDocumentTextExtractor.FigureImagePrefix + "*.*")
            .Select(path => (Path: path, Number: ParseAssetNumber(path, WordDocumentTextExtractor.FigureImagePrefix)))
            .Where(x => x.Number > 0)
            .OrderBy(x => x.Number);

        foreach (var figure in figures)
            yield return (figure.Path, WordDocumentTextExtractor.MediaTypeForFigureFile(figure.Path));
    }

    private static int ParseAssetNumber(string path, string prefix)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(name[prefix.Length..], out var n) ? n : 0;
    }
}
