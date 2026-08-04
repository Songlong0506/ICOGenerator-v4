using System.Buffers.Binary;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using V = DocumentFormat.OpenXml.Vml;

namespace ICOGenerator.Services.Requirements;

/// <summary>Kết quả bóc một file Word: text (null nếu không có chữ nào) + số hình nhúng đã lấy ra được.</summary>
public sealed record WordExtraction(string? Text, int ImageCount);

/// <summary>
/// Bóc một file Word (.docx / .docm) cho BA đọc: TEXT (từng đoạn văn theo thứ tự, bảng render thành dòng
/// "ô | ô | ô" — giữ được cấu trúc biểu mẫu) VÀ các HÌNH NHÚNG đáng giá (screenshot màn hình, sơ đồ quy
/// trình, biểu mẫu chụp lại) ghi ra <c>figure-{n}.png</c> cạnh file gốc để model vision đọc — cùng cách
/// PDF scan lấy ảnh trang (<see cref="PdfScanPageRenderer"/>). Tại vị trí mỗi hình, text có mốc
/// "[Hình {n}]" để model đối chiếu hình với đoạn mô tả xung quanh nó.
///
/// Vì sao cần: tài liệu kỹ thuật/nghiệp vụ của phòng ban rất hay nhét thông tin quan trọng vào ảnh
/// (chụp màn hình phần mềm cũ, sơ đồ nghiệp vụ, biểu mẫu scan) — chỉ bóc text là mất trọn phần đó.
/// Hình trang trí (icon, bullet, đường kẻ) bị lọc theo kích thước pixel để không đốt ngữ cảnh vision
/// vào ảnh vô nghĩa. Cùng tinh thần best-effort như <see cref="SpreadsheetTextExtractor"/>: file
/// hỏng/không đọc được ⇒ trả rỗng, caller giữ file gốc.
/// </summary>
public static class WordDocumentTextExtractor
{
    // Trần để một tài liệu dài không nuốt trọn cửa sổ ngữ cảnh — BA cần nắm cấu trúc và các quy tắc chính,
    // không cần nguyên văn cả cuốn quy chế.
    private const int MaxBlocks = 400;
    private const int MaxCharsPerBlock = 2000;
    private const int MaxTotalChars = 40000;

    /// <summary>Tiền tố tên file hình nhúng trong thư mục của nguồn — <see cref="SourceContextBuilder"/> tìm theo nó.</summary>
    public const string FigureImagePrefix = "figure-";

    /// <summary>
    /// Trần số hình lấy ra mỗi tài liệu. Cao hơn trần gửi mỗi lượt gọi (Llm:SourceUpload:MaxImagesPerCall)
    /// một chút để còn dư địa khi người dùng nâng config; vượt trần thì GIỮ các hình lớn nhất (screenshot
    /// thật thường lớn hơn hẳn hình minh hoạ phụ), vẫn theo thứ tự tài liệu.
    /// </summary>
    public const int MaxImages = 12;

    // Hình nhỏ hơn ngần này pixel coi là icon/trang trí (bullet, logo con, đường kẻ), không phải screenshot
    // hay sơ đồ. Không đọc được kích thước (định dạng lạ) thì rơi về ngưỡng dung lượng byte.
    private const int MinImagePixels = 50_000; // ~ 250×200
    private const int MinBytesWhenSizeUnknown = 8 * 1024;

    public static readonly string[] Extensions = { ".docx", ".docm" };

    public static bool IsWordDocument(string? contentType, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (Extensions.Contains(ext))
            return true;
        var ct = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        return ct.Contains("wordprocessingml");
    }

    /// <summary>Bóc text từ bytes, không lấy hình. Trả null nếu không đọc được gì.</summary>
    public static string? Extract(byte[] bytes) => Extract(bytes, imageTargetDir: null).Text;

    /// <summary>
    /// Bóc text + hình nhúng. Hình được ghi thành <c>figure-{n}.png/.jpeg/…</c> vào
    /// <paramref name="imageTargetDir"/> (null ⇒ chỉ bóc text). Trả Text=null nếu tài liệu không có chữ
    /// (vd toàn ảnh scan dán vào Word) — khi đó ImageCount vẫn có thể &gt; 0 và nội dung đi bằng đường vision.
    /// </summary>
    public static WordExtraction Extract(byte[] bytes, string? imageTargetDir)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var doc = WordprocessingDocument.Open(ms, false);
            var mainPart = doc.MainDocumentPart;
            var body = mainPart?.Document?.Body;
            if (mainPart == null || body == null)
                return new WordExtraction(null, 0);

            // lines: từng dòng text theo thứ tự tài liệu; candidates: hình gặp trên đường đi, nhớ VỊ TRÍ dòng
            // để sau khi chọn lọc còn chèn mốc "[Hình n]" đúng chỗ.
            var lines = new List<string>();
            var candidates = new List<FigureCandidate>();
            var seenRelIds = new HashSet<string>(StringComparer.Ordinal);
            var blocks = 0;
            var totalChars = 0;

            // Duyệt con TRỰC TIẾP của body theo thứ tự tài liệu để đoạn văn và bảng giữ đúng trình tự —
            // lấy riêng Paragraph rồi riêng Table sẽ dồn hết bảng xuống cuối, mất mạch "mô tả rồi tới biểu mẫu".
            foreach (var element in body.ChildElements)
            {
                if (blocks >= MaxBlocks || totalChars >= MaxTotalChars)
                    break;

                switch (element)
                {
                    case Paragraph paragraph:
                        {
                            var text = Clean(paragraph.InnerText);
                            if (text.Length > 0)
                            {
                                lines.Add(text);
                                totalChars += text.Length;
                                blocks++;
                            }
                            break;
                        }
                    case Table table:
                        {
                            var rows = 0;
                            foreach (var row in table.Elements<TableRow>())
                            {
                                if (blocks >= MaxBlocks || totalChars >= MaxTotalChars)
                                    break;
                                var cells = row.Elements<TableCell>().Select(c => Clean(c.InnerText));
                                var line = string.Join(" | ", cells).Trim();
                                if (line.Replace("|", string.Empty).Trim().Length == 0)
                                    continue;
                                lines.Add(line);
                                totalChars += line.Length;
                                blocks++;
                                rows++;
                            }
                            if (rows > 0)
                                lines.Add(string.Empty);
                            break;
                        }
                    default:
                        continue;
                }

                if (imageTargetDir != null)
                    CollectFigures(element, mainPart, lines.Count, seenRelIds, candidates);
            }

            var written = imageTargetDir == null
                ? 0
                : WriteFiguresAndInsertMarkers(candidates, lines, imageTargetDir);

            var hasText = lines.Any(l => l.Length > 0 && !l.StartsWith("[Hình ", StringComparison.Ordinal));
            if (!hasText)
                return new WordExtraction(null, written);

            var result = string.Join("\n", lines).Trim();
            if (result.Length > MaxTotalChars)
                result = result[..MaxTotalChars] + "\n…(đã cắt bớt)";
            return new WordExtraction(result.Length == 0 ? null : result, written);
        }
        catch
        {
            return new WordExtraction(null, 0); // best-effort: hỏng thì bỏ qua, giữ file gốc.
        }
    }

    private sealed class FigureCandidate
    {
        public required string RelId { get; init; }
        public required int LineIndex { get; init; } // chèn mốc [Hình n] NGAY SAU dòng này
        public required byte[] Bytes { get; init; }
        public required string Extension { get; init; }
        public long Pixels { get; init; } // 0 = không đọc được kích thước
    }

    // Gom các hình nhúng trong một block (đoạn văn/bảng): DrawingML (a:blip r:embed — Word hiện đại) và
    // VML (v:imagedata r:id — file cũ/paste từ Word đời trước). Lọc ngay tại đây: đúng định dạng ảnh mà
    // model vision nhận (PNG/JPEG/GIF/WebP — EMF/WMF vector thì chịu) và đủ lớn để không phải icon.
    private static void CollectFigures(
        DocumentFormat.OpenXml.OpenXmlElement block, MainDocumentPart mainPart, int lineIndex,
        HashSet<string> seenRelIds, List<FigureCandidate> candidates)
    {
        foreach (var relId in FindImageRelIds(block))
        {
            if (string.IsNullOrEmpty(relId) || !seenRelIds.Add(relId))
                continue; // cùng một ảnh lặp lại (logo dán nhiều lần) chỉ tính một lần.

            try
            {
                if (mainPart.GetPartById(relId) is not ImagePart imagePart)
                    continue;
                var ext = ExtensionFor(imagePart.ContentType);
                if (ext == null)
                    continue;

                using var partStream = imagePart.GetStream();
                using var buffer = new MemoryStream();
                partStream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                if (bytes.Length == 0)
                    continue;

                var pixels = TryReadPixelCount(bytes);
                var keep = pixels > 0 ? pixels >= MinImagePixels : bytes.Length >= MinBytesWhenSizeUnknown;
                if (!keep)
                    continue;

                candidates.Add(new FigureCandidate
                {
                    RelId = relId,
                    LineIndex = lineIndex,
                    Bytes = bytes,
                    Extension = ext,
                    Pixels = pixels,
                });
            }
            catch
            {
                // Part hỏng/thiếu: bỏ qua hình đó, không hỏng cả tài liệu.
            }
        }
    }

    // Chọn tối đa MaxImages hình (ưu tiên hình LỚN nhất khi vượt trần — screenshot thật thường lớn hơn hẳn
    // hình phụ), đánh số theo thứ tự tài liệu, ghi file figure-{n}.{ext} và chèn mốc "[Hình n]" vào text.
    // Chèn từ dưới lên để chỉ số dòng của các mốc phía trên không bị lệch.
    private static int WriteFiguresAndInsertMarkers(
        List<FigureCandidate> candidates, List<string> lines, string imageTargetDir)
    {
        if (candidates.Count == 0)
            return 0;

        var selected = candidates.Count <= MaxImages
            ? candidates
            : candidates
                .OrderByDescending(c => c.Pixels > 0 ? c.Pixels : c.Bytes.Length)
                .Take(MaxImages)
                .OrderBy(c => candidates.IndexOf(c))
                .ToList();

        var written = 0;
        var markers = new List<(int LineIndex, string Marker)>();
        foreach (var figure in selected)
        {
            try
            {
                var n = written + 1;
                File.WriteAllBytes(
                    Path.Combine(imageTargetDir, $"{FigureImagePrefix}{n}.{figure.Extension}"), figure.Bytes);
                markers.Add((figure.LineIndex, $"[Hình {n} — trích từ tài liệu, gửi kèm dưới dạng ảnh]"));
                written++;
            }
            catch
            {
                // Ghi file lỗi (đĩa đầy…): bỏ qua hình đó.
            }
        }

        // markers đã theo thứ tự tài liệu (LineIndex không giảm). Chèn từ CUỐI lên để chỉ số các mốc phía
        // trên không lệch, và hai hình cùng một vị trí giữ đúng thứ tự [Hình 4] rồi [Hình 5].
        for (var i = markers.Count - 1; i >= 0; i--)
            lines.Insert(Math.Min(markers[i].LineIndex, lines.Count), markers[i].Marker);

        return written;
    }

    // Tìm rel-id ảnh ở mọi độ sâu trong block. So theo LOCAL NAME chứ không chỉ theo lớp typed: phần tử
    // nằm trong ngữ cảnh SDK không nhận ra (vd bên trong mc:AlternateContent/Fallback) bị parse thành
    // OpenXmlUnknownElement — Descendants&lt;Blip&gt;() sẽ bỏ sót dù ảnh hoàn toàn hợp lệ.
    private static IEnumerable<string?> FindImageRelIds(DocumentFormat.OpenXml.OpenXmlElement block)
    {
        foreach (var e in block.Descendants())
        {
            switch (e)
            {
                case A.Blip blip:
                    yield return blip.Embed?.Value;
                    break;
                case V.ImageData imageData:
                    yield return imageData.RelationshipId?.Value;
                    break;
                default:
                    if (e.LocalName is "blip" or "imagedata")
                        yield return e.GetAttributes()
                            .FirstOrDefault(a => a.LocalName is "embed" or "id" or "link").Value;
                    break;
            }
        }
    }

    // Chỉ các định dạng model vision nhận trực tiếp; EMF/WMF/TIFF/BMP trả null (bỏ qua).
    private static string? ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" or "image/jpg" => "jpeg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => null,
    };

    /// <summary>Media type để gửi cho model, suy từ đuôi file figure-{n}.{ext} đã ghi.</summary>
    public static string MediaTypeForFigureFile(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    // Đọc width×height từ header PNG/JPEG/GIF (không cần decode cả ảnh, không kéo thư viện đồ hoạ).
    // Trả 0 nếu không nhận ra định dạng — caller rơi về ngưỡng dung lượng byte.
    private static long TryReadPixelCount(byte[] bytes)
    {
        try
        {
            // PNG: chữ ký 8 byte, IHDR bắt đầu ở offset 8 (length 4 + "IHDR" 4), width/height big-endian ở 16/20.
            if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                var w = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
                var h = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
                return (long)w * h;
            }

            // GIF: "GIF87a"/"GIF89a", width/height little-endian ở 6/8.
            if (bytes.Length >= 10 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            {
                var w = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2));
                var h = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
                return (long)w * h;
            }

            // JPEG: duyệt các segment FF xx tới SOFn (C0–CF trừ C4/C8/CC), height/width big-endian ở +5/+7.
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                var i = 2;
                while (i + 9 < bytes.Length)
                {
                    if (bytes[i] != 0xFF)
                    {
                        i++;
                        continue;
                    }
                    var marker = bytes[i + 1];
                    if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    {
                        var h = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i + 5, 2));
                        var w = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i + 7, 2));
                        return (long)w * h;
                    }
                    var len = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i + 2, 2));
                    i += 2 + len;
                }
            }
        }
        catch
        {
            // Header lạ/cụt: coi như không đọc được kích thước.
        }
        return 0;
    }

    private static string Clean(string? raw)
    {
        var text = (raw ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ");
        return text.Length > MaxCharsPerBlock ? text[..MaxCharsPerBlock] + "…" : text;
    }
}
