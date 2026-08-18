using System.Security.Cryptography;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ICOGenerator.Services.Requirements;

/// <summary>Một hình đã ghi ra đĩa: số thứ tự trong tài liệu và trang chứa nó (để chèn mốc <c>[Hình n]</c>).</summary>
public sealed record PdfFigure(int Number, int PageNumber);

/// <summary>
/// Lấy các HÌNH NHÚNG trong những trang PDF CÓ TEXT (sơ đồ quy trình, ảnh chụp màn hình phần mềm cũ, biểu
/// mẫu chụp lại dán vào tài liệu) ra <c>figure-{n}.png</c> cạnh file gốc cho model vision đọc — đúng cách
/// <see cref="WordDocumentTextExtractor"/> làm với .docx.
///
/// Vì sao tách khỏi <see cref="PdfScanPageRenderer"/>: hai lớp giải hai bài khác nhau trên hai tập trang
/// RỜI NHAU. PdfScanPageRenderer cứu trang KHÔNG bóc được chữ, giả định "một trang scan = một ảnh phủ kín
/// trang" nên chỉ lấy ảnh LỚN NHẤT và đòi ảnh thật to. Lớp này lo trang CÓ chữ, nơi chữ đã đi bằng text và
/// phần còn thiếu là các hình minh hoạ nằm rải trong trang — nên lấy MỌI hình đủ lớn, ngưỡng thấp hơn.
/// Nhét chung một lớp thì một trong hai giả định phải sai.
///
/// Vì sao đáng làm: cùng một tài liệu, lưu .docx thì hình được gửi cho model (Word đã bóc từ lâu), xuất ra
/// .pdf thì mất trắng — trong khi PDF mới là định dạng người dùng nghiệp vụ hay gửi nhất.
/// </summary>
public static class PdfFigureExtractor
{
    /// <summary>Tiền tố tên file hình trong thư mục của nguồn — <see cref="SourceContextBuilder"/> tìm theo nó.</summary>
    public const string FigureImagePrefix = "figure-";

    /// <summary>
    /// Trần số hình lấy ra mỗi tài liệu, bằng trần của Word. Vượt trần thì GIỮ các hình lớn nhất (sơ đồ và
    /// ảnh màn hình thật thường lớn hơn hẳn hình minh hoạ phụ), rồi trả về đúng thứ tự tài liệu.
    /// </summary>
    public const int MaxImages = 12;

    /// <summary>
    /// Hình nhỏ hơn ngần này pixel coi là logo/icon/đường kẻ trang trí. Thấp hơn hẳn ngưỡng của
    /// <see cref="PdfScanPageRenderer"/> (200k px) vì ở đây không đòi ảnh phủ kín trang — một sơ đồ chiếm
    /// nửa trang A4 vẫn là thứ đáng gửi. Bằng ngưỡng của Word để cùng một tài liệu ở hai định dạng cho ra
    /// cùng bộ hình.
    /// </summary>
    private const int MinFigurePixels = 50_000; // ~ 250×200

    /// <summary>
    /// Trần số hình được GIẢI MÃ mỗi tài liệu. Dựng PNG là phần đắt nhất của cả bước bóc, nên một PDF vài
    /// trăm trang toàn ảnh không được phép kéo dài thời gian upload vô hạn: quá trần thì thôi thu thập và
    /// chọn trong những gì đã có. Cao hơn <see cref="MaxImages"/> đủ để phép chọn "giữ hình lớn nhất" còn
    /// có cái để chọn.
    /// </summary>
    private const int MaxDecodedCandidates = 40;

    /// <summary>Ứng viên đã giải mã: giữ PNG + kích thước để chọn lọc sau khi duyệt hết tài liệu.</summary>
    public sealed record Candidate(int PageNumber, byte[] Png, long Pixels);

    /// <summary>
    /// Gom hình nhúng của MỘT trang (trang đã bóc được text) vào <paramref name="candidates"/>. Gọi trong
    /// lúc document còn mở. Best-effort: hình nào lỗi thì bỏ hình đó.
    ///
    /// <paramref name="seenHashes"/> dùng chung cho cả tài liệu để logo/header lặp trên mọi trang chỉ tính
    /// một lần — đây cũng là thứ giữ chi phí xuống: băm dữ liệu THÔ trước, nên bản lặp không phải giải mã.
    /// </summary>
    public static void CollectPageFigures(Page page, List<Candidate> candidates, HashSet<string> seenHashes)
    {
        if (candidates.Count >= MaxDecodedCandidates)
            return;

        foreach (var image in page.GetImages())
        {
            if (candidates.Count >= MaxDecodedCandidates)
                return;

            try
            {
                var pixels = (long)image.WidthInSamples * image.HeightInSamples;
                if (pixels < MinFigurePixels)
                    continue;

                // Băm bytes thô (chưa giải mã) — bản lặp bị loại trước khi tốn công dựng PNG.
                var raw = image.RawMemory;
                if (raw.Length == 0 || !seenHashes.Add(Convert.ToHexString(SHA256.HashData(raw.Span))))
                    continue;

                if (!image.TryGetPng(out var png) || png is not { Length: > 0 })
                    continue;

                candidates.Add(new Candidate(page.Number, png, pixels));
            }
            catch
            {
                // Ảnh nén kiểu PdfPig không dựng được PNG, XObject hỏng…: bỏ qua hình đó.
            }
        }
    }

    /// <summary>
    /// Chọn tối đa <see cref="MaxImages"/> hình (ưu tiên hình lớn nhất khi vượt trần), ghi
    /// <c>figure-{n}.png</c> vào <paramref name="targetDir"/> và trả về danh sách đã ghi theo thứ tự tài
    /// liệu để caller chèn mốc <c>[Hình n]</c> vào đúng trang.
    /// </summary>
    public static IReadOnlyList<PdfFigure> WriteFigures(
        IReadOnlyList<Candidate> candidates, string targetDir, CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
            return Array.Empty<PdfFigure>();

        var selected = candidates.Count <= MaxImages
            ? candidates
            : candidates
                .Select((c, index) => (Candidate: c, Index: index))
                .OrderByDescending(x => x.Candidate.Pixels)
                .Take(MaxImages)
                .OrderBy(x => x.Index)
                .Select(x => x.Candidate)
                .ToList();

        var written = new List<PdfFigure>();
        foreach (var candidate in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var number = written.Count + 1;
                File.WriteAllBytes(Path.Combine(targetDir, $"{FigureImagePrefix}{number}.png"), candidate.Png);
                written.Add(new PdfFigure(number, candidate.PageNumber));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Ghi file lỗi (đĩa đầy…): bỏ qua hình đó, số thứ tự vẫn liền mạch theo số hình ĐÃ ghi.
            }
        }

        return written;
    }
}
