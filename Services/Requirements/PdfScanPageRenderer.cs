using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Cứu các trang PDF DẠNG SCAN (không bóc được text) bằng cách lấy ẢNH nhúng của trang ra thành PNG để
/// gửi cho model có vision — thay vì bỏ trắng nội dung như trước.
///
/// Vì sao đáng làm: người dùng nghiệp vụ thường đính kèm đúng thứ quý nhất dưới dạng scan (biểu mẫu đang
/// dùng, quy định đã ký, sổ tay quy trình). Trước đây hệ thống chỉ hiện cảnh báo "không đọc được chữ bên
/// trong" và bắt họ tự gõ lại — trong khi model BA thường ĐÃ có vision (cờ <c>AiModel.SupportsVision</c>,
/// UI đã dùng cờ này để cảnh báo về ảnh).
///
/// Cách làm cố ý ở mức RẺ: một trang scan điển hình là MỘT ảnh nhúng phủ gần kín trang, nên chỉ cần lấy
/// ảnh nhúng lớn nhất của trang đó ra — không kéo thêm engine rasterize PDF nào (PdfPig không rasterize).
/// Trang có nhiều ảnh vụn (chữ ký, logo) hay không có ảnh nào thì bỏ qua: thà thiếu một trang còn hơn nhét
/// vào ngữ cảnh vision một đống mảnh ảnh vô nghĩa.
/// </summary>
public static class PdfScanPageRenderer
{
    /// <summary>Tiền tố tên file ảnh trang trong thư mục của nguồn — <see cref="SourceContextBuilder"/> tìm theo nó.</summary>
    public const string PageImagePrefix = "page-";

    /// <summary>Ảnh nhỏ hơn ngần này pixel coi là logo/chữ ký, không phải bản scan của cả trang.</summary>
    private const int MinPageImagePixels = 200_000; // ~ 500×400

    /// <summary>Trần số trang được lấy ảnh: ngữ cảnh vision đắt, và SourceContextBuilder cũng có trần riêng.</summary>
    public const int MaxPages = 10;

    /// <summary>
    /// Ghi ảnh PNG cho các trang scan vào <paramref name="targetDir"/> (tên <c>page-{n}.png</c>) và trả về
    /// số trang đã ghi. <paramref name="scannedPageNumbers"/> là các trang mà bước bóc text đã bỏ qua.
    /// Best-effort toàn phần: trang nào lỗi thì bỏ qua trang đó, không ném ra ngoài.
    /// </summary>
    public static int TryRenderScannedPages(
        PdfDocument document,
        IReadOnlyCollection<int> scannedPageNumbers,
        string targetDir,
        CancellationToken cancellationToken = default)
    {
        if (scannedPageNumbers.Count == 0)
            return 0;

        var written = 0;
        foreach (var pageNumber in scannedPageNumbers.OrderBy(n => n).Take(MaxPages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var page = document.GetPage(pageNumber);
                var best = PickPageImage(page);
                if (best == null || !best.TryGetPng(out var png) || png is not { Length: > 0 })
                    continue;

                File.WriteAllBytes(Path.Combine(targetDir, $"{PageImagePrefix}{pageNumber}.png"), png);
                written++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Trang lỗi (ảnh nén kiểu PdfPig không dựng được PNG, XObject hỏng…): bỏ qua trang đó.
            }
        }

        return written;
    }

    // Ảnh nhúng LỚN NHẤT của trang, và chỉ khi nó đủ lớn để là bản scan cả trang.
    private static IPdfImage? PickPageImage(Page page)
    {
        IPdfImage? best = null;
        var bestPixels = 0L;

        foreach (var image in page.GetImages())
        {
            var pixels = (long)image.WidthInSamples * image.HeightInSamples;
            if (pixels <= bestPixels)
                continue;
            best = image;
            bestPixels = pixels;
        }

        return bestPixels >= MinPageImagePixels ? best : null;
    }
}
