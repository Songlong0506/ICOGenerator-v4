using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Biến các <see cref="ProjectSourceFile"/> của một project thành danh sách <see cref="AIContent"/> để gắn kèm
/// lượt user khi gọi LLM: <see cref="TextContent"/> cho text bóc từ PDF, <see cref="DataContent"/> cho ảnh người
/// dùng upload trực tiếp. PDF chỉ đóng góp text (PDF scan/ảnh không có text bị bỏ qua, không render). Phần ảnh CHỈ
/// được thêm khi model hỗ trợ vision; model text-only chỉ nhận text. Áp trần số ảnh + tổng dung lượng ảnh ngay tại
/// đây để chặn đốt token ngoài kiểm soát.
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
                contents.Add(new TextContent(header + "\n" + text));
            }
            else
            {
                contents.Add(new TextContent(header + (s.Kind switch
                {
                    SourceFileKind.Image => " (ảnh — xem nội dung ảnh đính kèm)",
                    SourceFileKind.Spreadsheet => " (bảng tính — không đọc được nội dung, đã bỏ qua)",
                    _ => " (PDF dạng scan/ảnh — không trích xuất được text, nội dung bị bỏ qua)"
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

    // Chỉ ảnh user upload trực tiếp mới đóng góp phần vision; PDF chỉ đóng góp text (app đã bỏ render PDF→ảnh).
    private static IEnumerable<(string Path, string MediaType)> EnumerateImageAssets(ProjectSourceFile s)
    {
        if (s.Kind == SourceFileKind.Image)
            yield return (s.StoredPath, string.IsNullOrWhiteSpace(s.ContentType) ? "image/png" : s.ContentType);
    }
}
