using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Tài liệu nguồn (ảnh / PDF) người dùng upload vào một project để agent BA dùng làm ngữ cảnh khi chat
/// và khi sinh tài liệu requirement. Khác với <see cref="ProjectDocument"/> (vốn là OUTPUT đã sinh):
/// đây là INPUT do người dùng cung cấp. File gốc lưu trên đĩa workspace (<see cref="StoredPath"/>); DB chỉ
/// giữ metadata + phần text đã bóc (<see cref="ExtractedText"/>) và đường dẫn các ảnh trang scan đã render.
/// </summary>
public class ProjectSourceFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    public SourceFileKind Kind { get; set; }

    /// <summary>Tên file gốc do người dùng đặt (để hiển thị).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>MIME type (vd image/png, application/pdf) — dùng làm media type khi gửi cho model vision.</summary>
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Đường dẫn tuyệt đối tới file gốc đã lưu trong workspace project.</summary>
    public string StoredPath { get; set; } = string.Empty;

    /// <summary>Text bóc từ PDF (null với ảnh, hoặc PDF scan/ảnh không có text — loại này không được hỗ trợ).</summary>
    public string? ExtractedText { get; set; }

    /// <summary>Số trang (với PDF). 0 với ảnh.</summary>
    public int PageCount { get; set; }

    /// <summary>
    /// BẢNG CỘT đã được người dùng chốt cho nguồn dạng bảng tính: JSON array
    /// <see cref="Contracts.Requirements.SourceColumnNote"/> (tên cột + ý nghĩa + có dùng hay không).
    /// Null = chưa chốt (nguồn không phải bảng tính, hoặc người dùng chưa gửi bảng).
    ///
    /// <para>
    /// Đây là thứ biến bảng cột từ một màn bấm đẹp thành dữ liệu thật: <see cref="Services.Requirements.SourceContextBuilder"/>
    /// gắn nó vào ngữ cảnh mọi lượt chat sau (BA thôi hỏi lại nghĩa các cột đã chốt), còn
    /// <see cref="Services.Requirements.RealSampleDataReader"/> LỌC các dòng dữ liệu mẫu theo đúng tập cột
    /// này trước khi chúng đi vào prompt AI Design Spec và POC seed — không có bước lọc đó thì người dùng
    /// mở demo ra vẫn thấy <c>Revision Number</c> nằm như một trường của app mới.
    /// </para>
    ///
    /// <para>
    /// KHÔNG mã hóa at rest, khác các cột hội thoại: <see cref="ExtractedText"/> — toàn bộ nội dung file —
    /// nằm ngay cạnh dưới dạng plaintext, nên mã hóa riêng bản đồ cột không che thêm được gì.
    /// </para>
    /// </summary>
    public string? ColumnMap { get; set; }

    /// <summary>
    /// True nếu nguồn này có phần ẢNH cần model vision: file ảnh upload trực tiếp, PDF đã lấy được ảnh trang
    /// scan hoặc hình nhúng, hoặc Word có hình nhúng đã lấy ra (<see cref="ScannedPageImageCount"/> &gt; 0).
    /// </summary>
    public bool IsVisionSource { get; set; }

    /// <summary>
    /// TỔNG số ảnh PNG đã lấy ra từ nguồn, nằm cạnh file gốc: trang SCAN của PDF (tên <c>page-{n}.png</c> —
    /// xem <see cref="Services.Requirements.PdfScanPageRenderer"/>), hình nhúng trong trang PDF CÓ chữ (tên
    /// <c>figure-{n}.png</c> — xem <see cref="Services.Requirements.PdfFigureExtractor"/>), hoặc hình nhúng
    /// trong Word (cũng <c>figure-{n}.png</c> — xem <see cref="Services.Requirements.WordDocumentTextExtractor"/>).
    /// Tên cột giữ nguyên vì đã có dữ liệu trong DB, và một PDF có thể góp cả hai loại vào cùng con số này —
    /// <see cref="Services.Requirements.SourceContextBuilder"/> chỉ cần biết TỔNG để nói đúng số ảnh gửi kèm.
    /// 0 = không có ảnh nào lấy được — với PDF scan khi đó nội dung thực sự bị bỏ qua và người dùng được cảnh báo.
    /// </summary>
    public int ScannedPageImageCount { get; set; }

    /// <summary>
    /// Nội dung các HÌNH của nguồn này đã được BA đọc trực tiếp từ ảnh và ghi lại thành chữ (lượt xác nhận
    /// tài liệu — xem <c>Prompts/BusinessAnalyst/source-ack.v3.md</c>). Null = chưa mô tả, ảnh vẫn phải gửi
    /// kèm khi gọi model.
    ///
    /// Đây là thứ cắt chi phí vision của cả hội thoại: ảnh vốn được đính vào MỖI lượt chat (mỗi request là
    /// một lần upload lại), nên một cuộc chat 20 lượt trả tiền 20 lần cho cùng bộ ảnh. Có bản mô tả rồi thì
    /// ảnh đi đúng một lần, các lượt sau chỉ mang phần chữ này — xem <see cref="Services.Requirements.SourceContextBuilder"/>.
    /// Chỉ được ghi khi TOÀN BỘ hình của nguồn thực sự đã đi kèm lượt đó; mô tả dựa trên nửa số hình rồi
    /// khóa lại là mất trắng phần còn lại.
    /// </summary>
    public string? VisionSummary { get; set; }

    public string? UploadedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
