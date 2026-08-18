using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Kết quả dựng ngữ cảnh nguồn cho một lời gọi model: phần <see cref="Contents"/> gắn vào lượt user, và
/// <see cref="FullyAttachedSourceIds"/> — các nguồn có TOÀN BỘ ảnh của nó thực sự đi kèm lượt này, tức là
/// model đã nhìn thấy đủ để mô tả lại bằng chữ (xem <see cref="ProjectSourceFile.VisionSummary"/>). Nguồn
/// chỉ đi kèm được một phần ảnh KHÔNG nằm trong danh sách này: mô tả dựa trên nửa số hình rồi khóa lại là
/// mất trắng phần còn lại.
/// </summary>
public sealed record SourceContext(List<AIContent> Contents, IReadOnlyList<Guid> FullyAttachedSourceIds)
{
    public static readonly SourceContext Empty = new(new List<AIContent>(), Array.Empty<Guid>());

    public int Count => Contents.Count;
}

/// <summary>
/// Biến các <see cref="ProjectSourceFile"/> của một project thành danh sách <see cref="AIContent"/> để gắn kèm
/// lượt user khi gọi LLM: <see cref="TextContent"/> cho text đã bóc, <see cref="DataContent"/> cho phần ảnh —
/// ảnh người dùng upload trực tiếp, ảnh trang PDF scan (page-{n}.png), và hình nhúng bóc từ PDF hoặc Word
/// (figure-{n}.*).
/// Phần ảnh CHỈ được thêm khi model hỗ trợ vision; model text-only chỉ nhận text. Áp trần số ảnh + tổng dung
/// lượng ảnh ngay tại đây để chặn đốt token ngoài kiểm soát.
///
/// Ảnh KHÔNG được gửi lại mãi mãi: nguồn nào đã có <see cref="ProjectSourceFile.VisionSummary"/> (bản ghi
/// lại nội dung hình bằng chữ, do BA đọc ảnh ở lượt xác nhận tài liệu) thì lượt sau chỉ gửi phần chữ đó.
/// Đây là điều làm cho trần ảnh mỗi lượt không còn là trần "cả cuộc hội thoại": ảnh đi một lần, chữ đi mãi.
/// Hệ quả phụ có chủ đích: hạn mức ảnh của lượt sau dồn cho các nguồn CHƯA được mô tả, nên một project
/// nhiều tài liệu tự cuốn chiếu hết chứ không kẹt mãi ở mấy file đầu.
///
/// Câu ghi chú kèm theo mỗi nguồn phải nói ĐÚNG số ảnh thực sự đi kèm. Nói "kèm 12 hình" rồi chỉ gửi 6 là
/// mời model bịa nội dung 6 hình còn lại — nên số trong câu chữ được tính SAU khi đã chốt danh sách ảnh.
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
        _maxImages = configuration.GetValue("Llm:SourceUpload:MaxImagesPerCall", 12);
        // Trần TỔNG dung lượng ảnh một lượt gọi — trần KÍCH THƯỚC GÓI TIN, không phải trần token (việc đó
        // do MaxImagesPerCall + VisionSummary lo). Ảnh đi trên dây dưới dạng base64 (+33%) trong MỘT request
        // JSON, nên endpoint nào có gateway/proxy chặn body lớn sẽ đóng thẳng kết nối giữa lúc đẩy, và lời
        // gọi chết mà không có mã HTTP nào để đọc lý do (xem EndpointQuirks.RequestNeverReachedModel). Hạ
        // trần này khi gặp đúng triệu chứng ĐÓ; mặc định giữ rộng vì các endpoint lớn nhận thoải mái, và
        // cắt bớt hình là cắt thẳng vào thứ BA đọc được từ tài liệu.
        _maxTotalImageBytes = configuration.GetValue("Llm:SourceUpload:MaxTotalImageBytes", 20L * 1024 * 1024);
        _maxTextCharsPerFile = configuration.GetValue("Llm:SourceUpload:MaxTextCharsPerFile", 20000);
    }

    /// <summary>Trả về <see cref="SourceContext.Empty"/> nếu không có nguồn (caller giữ nguyên message text thuần như cũ).</summary>
    public SourceContext Build(IEnumerable<ProjectSourceFile>? sources, bool modelSupportsVision)
    {
        var contents = new List<AIContent>();
        var fullyAttached = new List<Guid>();
        var list = sources?.OrderBy(s => s.CreatedAt).ToList() ?? new List<ProjectSourceFile>();
        if (list.Count == 0)
            return SourceContext.Empty;

        contents.Add(new TextContent(
            "\n\n=== TÀI LIỆU NGUỒN DO NGƯỜI DÙNG CUNG CẤP (tham khảo khi phân tích yêu cầu) ==="));

        var imageCount = 0;
        long imageBytes = 0;

        foreach (var s in list)
        {
            var expected = ExpectedImageCount(s);
            var summary = Truncate(s.VisionSummary);
            // Đã có bản mô tả hình bằng chữ ⇒ KHÔNG đọc lại ảnh (đây là chỗ tiết kiệm của cả cơ chế), và
            // hạn mức ảnh còn nguyên cho các nguồn chưa được mô tả.
            var images = modelSupportsVision && summary == null && expected > 0
                ? TakeImages(s, ref imageCount, ref imageBytes)
                : new List<DataContent>();

            var note = ImageNote(s.Kind, modelSupportsVision, expected, images.Count, summary != null);

            var text = Truncate(s.ExtractedText);
            contents.Add(new TextContent(text != null
                ? $"\n[Nguồn: {s.FileName}]{note}\n{text}"
                : $"\n[Nguồn: {s.FileName}]{TextlessSuffix(s.Kind, expected)}{note}"));

            if (summary != null)
                contents.Add(new TextContent(
                    $"\n--- Nội dung {expected} {Unit(s.Kind)} trong \"{s.FileName}\" (BA đã đọc trực tiếp từ ảnh và "
                    + "ghi lại; coi đây là thứ duy nhất biết được về chúng) ---\n" + summary));

            // BẢNG CỘT người dùng đã chốt cho nguồn này (xem SourceColumnMapBuilder). Đặt NGAY SAU phần text
            // của chính nguồn đó để nó đọc như một chú giải của bảng vừa đọc — và để BA thôi hỏi lại nghĩa
            // các cột người dùng vừa duyệt, thôi dựng yêu cầu trên các cột đã bị loại bỏ.
            var confirmedColumns = SourceColumnMapBuilder.RenderConfirmedBlock(s.FileName, s.ColumnMap);
            if (confirmedColumns != null)
                contents.Add(new TextContent("\n" + confirmedColumns));

            contents.AddRange(images);

            // Chỉ nguồn đi kèm ĐỦ ảnh mới được phép khóa lại thành mô tả chữ. Thiếu dù một hình thì để
            // nguyên: lượt sau nó vẫn được ưu tiên hạn mức ảnh cho tới khi đủ.
            if (summary == null && expected > 0 && images.Count == expected)
                fullyAttached.Add(s.Id);
        }

        return new SourceContext(contents, fullyAttached);
    }

    /// <summary>Số ảnh mà nguồn này ĐÁNG LẼ gửi kèm: ảnh upload trực tiếp là 1, PDF scan/Word là số hình đã bóc.</summary>
    private static int ExpectedImageCount(ProjectSourceFile s) =>
        s.Kind == SourceFileKind.Image ? 1 : s.ScannedPageImageCount;

    // Đọc ảnh của một nguồn trong phần hạn mức CÒN LẠI của lượt gọi (số ảnh + tổng byte, cộng dồn trên mọi
    // nguồn). Đọc bytes rồi mới quyết định là cố ý: caller cần biết CHÍNH XÁC bao nhiêu ảnh đi kèm trước khi
    // viết câu ghi chú, nên không thể ước lượng.
    private List<DataContent> TakeImages(ProjectSourceFile s, ref int imageCount, ref long imageBytes)
    {
        var images = new List<DataContent>();
        foreach (var (path, mediaType, name) in EnumerateImageAssets(s))
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
                // Name không đi tới model (phần ảnh của OpenAI không có chỗ cho tên file) — nó để CALL LOG
                // gọi được tên tấm ảnh, thay vì một danh sách "image-1, image-2" không truy được về file nào.
                images.Add(new DataContent(bytes, mediaType) { Name = name });
                imageCount++;
                imageBytes += bytes.Length;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Đọc ảnh nguồn {Path} thất bại; bỏ qua.", path);
            }
        }
        return images;
    }

    // Câu mô tả tình trạng phần ẢNH của một nguồn. Nguyên tắc xuyên suốt: chỉ mời model "xem ảnh đính kèm"
    // đúng số ảnh THẬT SỰ đi kèm; mọi hình không gửi được đều phải nói thẳng là không có, kèm lệnh cấm suy
    // đoán — model được bảo "có 12 hình" mà chỉ thấy 6 sẽ điền nốt 6 hình kia bằng tưởng tượng.
    private static string ImageNote(SourceFileKind kind, bool modelSupportsVision, int expected, int sent, bool hasSummary)
    {
        if (expected <= 0)
            return string.Empty;

        var unit = Unit(kind);
        // Mốc trong phần text để model ghép mỗi ảnh với đoạn mô tả quanh nó — mỗi loại nguồn một kiểu mốc.
        var marker = kind switch
        {
            SourceFileKind.Pdf => ", đối chiếu các mốc [Hình n] và \"--- Trang n ---\" trong text nếu có",
            SourceFileKind.Document => ", đối chiếu các mốc [Hình n] trong text nếu có",
            _ => string.Empty,
        };

        if (hasSummary)
            return $" ({expected} {unit} của nguồn này đã được đọc và ghi lại thành chữ ở phần "
                + $"\"Nội dung {expected} {unit}\" ngay bên dưới — ảnh KHÔNG gửi lại ở lượt này để tiết kiệm "
                + "ngữ cảnh; chỉ dựa vào phần chữ đó, KHÔNG suy đoán thêm chi tiết nào ngoài những gì đã ghi)";

        if (!modelSupportsVision)
            return $" (nguồn này có {expected} {unit} nhưng model hiện tại KHÔNG đọc được ảnh nên chúng KHÔNG "
                + "được gửi kèm; TUYỆT ĐỐI không tự suy đoán nội dung, hãy hỏi người dùng gõ/nhập lại các "
                + "thông tin trong đó)";

        if (sent == 0)
            return $" (nguồn này có {expected} {unit} nhưng đã hết hạn mức ảnh của lượt gọi này nên KHÔNG cái "
                + "nào được gửi kèm; TUYỆT ĐỐI không tự suy đoán nội dung)";

        if (sent < expected)
            return $" (kèm {sent}/{expected} {unit} dưới dạng ẢNH: CHỈ {unit} thứ 1–{sent} có ảnh đính "
                + $"kèm, {expected - sent} {unit} còn lại (từ thứ {sent + 1}) KHÔNG được gửi kèm — TUYỆT ĐỐI "
                + "không suy đoán nội dung phần không có ảnh, hãy hỏi lại người dùng)";

        return $" (kèm {expected} {unit} dưới dạng ẢNH — xem nội dung ảnh đính kèm{marker})";
    }

    // Đơn vị đếm phần ảnh của một nguồn. PDF cố tình dùng "hình" chung chứ không phải "trang scan": một PDF
    // có thể vừa có ảnh của trang bản scan vừa có hình nhúng bóc từ trang có chữ, và câu ghi chú chỉ mang
    // MỘT con số tổng — gọi tất cả là "trang scan" là nói sai về phần hình.
    private static string Unit(SourceFileKind kind) => kind switch
    {
        SourceFileKind.Image => "ảnh",
        _ => "hình",
    };

    // Phần mô tả cho nguồn KHÔNG bóc được chữ nào: nói rõ loại file và việc text bị bỏ qua. Phần ảnh do
    // ImageNote nói tiếp ngay sau — nguồn có ảnh thì nội dung ĐI KÈM dưới dạng ảnh chứ không "bị bỏ qua",
    // nói sai chỗ này là BA đi hỏi lại người dùng đúng thứ nó đang cầm trong tay.
    private static string TextlessSuffix(SourceFileKind kind, int expected) => kind switch
    {
        // Ảnh upload trực tiếp không có text để mà "bỏ qua" — ImageNote nói trọn tình trạng của nó.
        SourceFileKind.Image => string.Empty,
        SourceFileKind.Spreadsheet => " (bảng tính — không đọc được nội dung, đã bỏ qua)",
        SourceFileKind.Document => expected > 0
            ? " (tài liệu Word — không bóc được chữ nào, nội dung nằm ở phần hình)"
            : " (tài liệu Word — không đọc được nội dung, đã bỏ qua)",
        _ => expected > 0
            ? " (PDF dạng scan — không bóc được chữ nào, nội dung nằm ở ảnh các trang)"
            : " (PDF dạng scan/ảnh — không trích xuất được text, nội dung bị bỏ qua)",
    };

    private string? Truncate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return raw.Length > _maxTextCharsPerFile ? raw[.._maxTextCharsPerFile] + "\n…(đã cắt bớt)" : raw;
    }

    // Nguồn vision gồm: ảnh user upload trực tiếp, ảnh trang của PDF scan (PdfScanPageRenderer ghi
    // page-{n}.png cạnh file gốc), VÀ hình nhúng bóc từ trang có chữ của PDF (PdfFigureExtractor) hay từ
    // Word (WordDocumentTextExtractor) — cả hai ghi figure-{n}.*.
    // Ảnh xếp theo SỐ THỨ TỰ chứ không theo thứ tự chuỗi, để trang/hình 10 không nhảy lên trước 2 —
    // model đọc một biểu mẫu nhiều trang cần đúng trình tự.
    private static IEnumerable<(string Path, string MediaType, string Name)> EnumerateImageAssets(ProjectSourceFile s)
    {
        if (s.Kind == SourceFileKind.Image)
        {
            yield return (s.StoredPath, string.IsNullOrWhiteSpace(s.ContentType) ? "image/png" : s.ContentType, s.FileName);
            yield break;
        }

        if (s.Kind is not (SourceFileKind.Pdf or SourceFileKind.Document) || s.ScannedPageImageCount <= 0)
            yield break;

        var dir = Path.GetDirectoryName(s.StoredPath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            yield break;

        if (s.Kind == SourceFileKind.Pdf)
        {
            // Ảnh của các trang BẢN SCAN trước (theo số trang), rồi tới hình nhúng bóc từ các trang CÓ chữ
            // (theo số hình). Hai tập trang rời nhau nên thứ tự này chỉ khác thứ tự trang ở tài liệu vừa có
            // trang scan vừa có hình — và ở đó mỗi mốc [Hình n] đã tự nói ra nó thuộc trang nào.
            foreach (var page in EnumerateAssets(dir, PdfScanPageRenderer.PageImagePrefix, s.StoredPath))
                yield return (page.Path, "image/png", $"{s.FileName} › trang {page.Number}");

            foreach (var figure in EnumerateAssets(dir, PdfFigureExtractor.FigureImagePrefix, s.StoredPath))
                yield return (figure.Path, "image/png", $"{s.FileName} › Hình {figure.Number}");
            yield break;
        }

        foreach (var figure in EnumerateAssets(dir, WordDocumentTextExtractor.FigureImagePrefix, s.StoredPath))
            yield return (
                figure.Path,
                WordDocumentTextExtractor.MediaTypeForFigureFile(figure.Path),
                $"{s.FileName} › Hình {figure.Number}");
    }

    // Các file ảnh đã lấy ra (page-{n}.* / figure-{n}.*) trong thư mục nguồn, theo SỐ THỨ TỰ chứ không theo
    // thứ tự chuỗi. FILE GỐC bị loại trừ: người dùng hoàn toàn có thể upload một file tên "figure-1.pdf", và
    // nó nằm chung thư mục với các ảnh lấy ra — gửi nguyên file PDF/Word đi dưới media type image/png thì
    // lời gọi model hỏng mà không có gì chỉ ra vì sao.
    private static IEnumerable<(string Path, int Number)> EnumerateAssets(string dir, string prefix, string storedPath) => Directory
        .EnumerateFiles(dir, prefix + "*.*")
        .Where(path => !string.Equals(path, storedPath, StringComparison.Ordinal))
        .Select(path => (Path: path, Number: ParseAssetNumber(path, prefix)))
        .Where(x => x.Number > 0)
        .OrderBy(x => x.Number);

    private static int ParseAssetNumber(string path, string prefix)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(name[prefix.Length..], out var n) ? n : 0;
    }
}
