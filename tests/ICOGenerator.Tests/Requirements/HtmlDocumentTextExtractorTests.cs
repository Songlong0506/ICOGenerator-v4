using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Trang HTML — thường là mockup giao diện — phải ra được thứ BA dùng để phỏng vấn: màn hình (kể cả màn đang
// ẩn), trường/nút của form, bảng, nhãn đoạn do người dựng mockup đặt, và phần chữ nằm trong script. File
// "tự chứa" (bundled page) phải được mở gói, nếu không BA chỉ đọc được "Unpacking…".
public class HtmlDocumentTextExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ico-html-tests-" + Guid.NewGuid().ToString("N"));

    public HtmlDocumentTextExtractorTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData("mockup.html", null, true)]
    [InlineData("MOCKUP.HTM", null, true)]
    [InlineData("export", "text/html", true)]
    [InlineData("notes.txt", "text/plain", false)]
    public void IsHtmlDocument_ByExtensionOrContentType(string fileName, string? contentType, bool expected) =>
        Assert.Equal(expected, HtmlDocumentTextExtractor.IsHtmlDocument(contentType, fileName));

    [Fact]
    public void Extract_StaticMockup_KeepsScreensFieldsAndTables_EvenHiddenOnes()
    {
        const string html = """
            <!DOCTYPE html>
            <html><head><title>Quản lý tủ đồ</title><style>.card { color: red }</style></head>
            <body>
              <!-- ============ DASHBOARD ============ -->
              <h1>Tổng quan</h1>
              <section style="display:none" hidden>
                <!-- CẤP TỦ -->
                <h2>Cấp tủ mới</h2>
                <form>
                  <label>Mã nhân viên <input type="text" placeholder="VD: VN-100482"></label>
                  <label>Ngày bắt đầu <input type="date"></label>
                  <select name="cabinet"><option>Tủ giày</option><option>Tủ quần áo</option></select>
                  <label><input type="checkbox" checked> Cấp tạm thời</label>
                  <input type="hidden" name="token" value="bi-mat">
                  <button type="submit">Gửi yêu cầu</button>
                  <button type="button" aria-label="Đóng hộp thoại"><svg><path d="M0 0"/></svg></button>
                </form>
              </section>
              <table>
                <thead><tr><th>Mã tủ</th><th>Trạng thái</th></tr></thead>
                <tbody><tr><td>HcP-000101-001</td><td>Đang dùng <button>Thu hồi</button></td></tr></tbody>
              </table>
              <script>console.log("khởi động");</script>
            </body></html>
            """;

        var text = HtmlDocumentTextExtractor.Extract(Encoding.UTF8.GetBytes(html));

        Assert.NotNull(text);
        Assert.Contains("Tiêu đề trang: Quản lý tủ đồ", text);
        Assert.Contains("[Chú thích: DASHBOARD]", text);
        Assert.Contains("# Tổng quan", text);
        // Màn đang ẨN vẫn phải có: mockup nhiều màn giấu mọi màn trừ màn đầu.
        Assert.Contains("## Cấp tủ mới", text);
        Assert.Contains("[Chú thích: CẤP TỦ]", text);
        Assert.Contains("Mã nhân viên [Ô nhập: VD: VN-100482]", text);
        Assert.Contains("Ngày bắt đầu [Ô nhập date]", text);
        Assert.Contains("[Chọn (cabinet): Tủ giày / Tủ quần áo]", text);
        Assert.Contains("☑ Cấp tạm thời", text);
        Assert.Contains("[Nút: Gửi yêu cầu]", text);
        Assert.Contains("[Nút: Đóng hộp thoại]", text);
        Assert.Contains("Mã tủ | Trạng thái", text);
        Assert.Contains("HcP-000101-001 | Đang dùng [Nút: Thu hồi]", text);

        Assert.DoesNotContain("bi-mat", text);    // input hidden không phải trường người dùng thấy
        Assert.DoesNotContain("color: red", text); // CSS
        Assert.DoesNotContain("M0 0", text);       // SVG
    }

    [Fact]
    public void Extract_AdjacentInlineCells_DoNotRunTogether()
    {
        // Không có CSS thì không biết con của một lưới là từng ô — dính chữ ("TypeROP") tệ hơn một dấu cách thừa.
        const string html = "<div style=\"display:grid\"><span>Type</span><span>ROP</span><span>Lead time</span></div>";

        var text = HtmlDocumentTextExtractor.Extract(Encoding.UTF8.GetBytes(html));

        Assert.Equal("Type ROP Lead time", text);
    }

    [Fact]
    public void Extract_TemplateBindings_BecomeAngleBrackets()
    {
        // {{ … }} của template viết lại thành ⟨…⟩: prompt đọc nguồn mới gọi tên được nó mà không đụng luật
        // placeholder {{…}} của prompt (PromptConventionTests).
        const string html = "<h1>{{ pageTitle }}</h1><div>{{k.label}}: {{ k.value }}</div>";

        var text = HtmlDocumentTextExtractor.Extract(Encoding.UTF8.GetBytes(html));

        Assert.Equal("# ⟨pageTitle⟩\n⟨k.label⟩: ⟨k.value⟩", text);
    }

    [Fact]
    public void Extract_Scripts_TextComesBeforeCode_AndLibrariesAreSkipped()
    {
        // Mockup React/vanilla để menu, tiêu đề màn hình và thông báo trong JS. Phần CHỮ phải đứng trước mã
        // nguyên văn vì trần chữ mỗi nguồn cắt từ cuối; CSS/class trong JS và thư viện nén là nhiễu.
        var minified = "!function(){" + string.Concat(Enumerable.Repeat("var a='LibraryNoise';", 200)) + "}();";
        var html = $$"""
            <html><body>
              <div id="root"></div>
              <script src="https://unpkg.com/react.production.min.js"></script>
              <script>{{minified}}</script>
              <script type="text/babel">
                const menu = [
                  { id: 'dashboard', label: 'Bảng điều khiển' },
                  { id: 'requests', label: 'Yêu cầu cấp tủ' },
                ];
                const style = 'display:flex;gap:8px';
                const cls = 'flex items-center gap-2';
                function done(id) { alert(`Đã gửi yêu cầu ${id} tới FCM`); }
              </script>
            </body></html>
            """;

        var text = HtmlDocumentTextExtractor.Extract(Encoding.UTF8.GetBytes(html));

        Assert.NotNull(text);
        var textAt = text.IndexOf(HtmlDocumentTextExtractor.ScriptTextHeading, StringComparison.Ordinal);
        var codeAt = text.IndexOf(HtmlDocumentTextExtractor.ScriptCodeHeading, StringComparison.Ordinal);
        Assert.True(textAt >= 0 && codeAt > textAt, "Chữ trong script phải đứng TRƯỚC mã script.");

        var strings = text[textAt..codeAt];
        Assert.Contains("dashboard · Bảng điều khiển", strings);
        Assert.Contains("Yêu cầu cấp tủ", strings);
        Assert.Contains("Đã gửi yêu cầu … tới FCM", strings);
        Assert.DoesNotContain("display:flex", strings);
        Assert.DoesNotContain("items-center", strings);

        Assert.DoesNotContain("LibraryNoise", text);
        Assert.Contains("Đã bỏ qua 1 script dạng thư viện/đã nén", text);
    }

    [Fact]
    public void Extract_BundledPage_UnpacksTemplate_AndImageFromManifest()
    {
        // Định dạng "tự chứa": thân trang thật nằm trong chuỗi JSON của __bundler/template, ảnh nằm trong
        // __bundler/manifest (base64 + gzip) và được tham chiếu bằng uuid. Vỏ ngoài chỉ có "Unpacking…".
        const string imageId = "0f8a3c1e-1111-4222-8333-944455556666";
        var template = $$"""
            <!DOCTYPE html><html><head><title>Locker</title></head><body>
              <h2>Locker inventory</h2>
              <img src="{{imageId}}" alt="Sơ đồ phòng thay đồ">
              <script type="text/x-dc">class Component { titles = { dashboard: ['Dashboard'], inventory: ['Locker inventory'] }; }</script>
            </body></html>
            """;
        var manifest = new Dictionary<string, object>
        {
            [imageId] = new { mime = "image/png", compressed = true, data = Convert.ToBase64String(Gzip(PngHeader(400, 300))) },
        };
        var html = $"""
            <!DOCTYPE html><html><head><title>Bundled Page</title></head><body>
              <div id="__bundler_loading">Unpacking...</div>
              <script type="__bundler/manifest">{JsonSerializer.Serialize(manifest)}</script>
              <script type="__bundler/page_order">[]</script>
              <script type="__bundler/template">{JsonSerializer.Serialize(template)}</script>
            </body></html>
            """;

        var result = HtmlDocumentTextExtractor.Extract(Encoding.UTF8.GetBytes(html), _dir);

        Assert.NotNull(result.Text);
        Assert.Contains("Tiêu đề trang: Locker", result.Text);
        Assert.Contains("## Locker inventory", result.Text);
        Assert.Contains("[Ảnh: Sơ đồ phòng thay đồ]", result.Text);
        Assert.Contains("[Hình 1", result.Text);
        Assert.Contains("Dashboard", result.Text);
        Assert.DoesNotContain("Unpacking", result.Text);
        Assert.DoesNotContain("Bundled Page", result.Text);

        Assert.Equal(1, result.ImageCount);
        Assert.True(File.Exists(Path.Combine(_dir, "figure-1.png")));
    }

    [Fact]
    public void Extract_BundledPage_ReadsNestedPageFrame()
    {
        // Trang con của bundle đi qua iframe "about:blank#{uuid}" và text nằm trong manifest.
        const string pageId = "1a2b3c4d-aaaa-4bbb-8ccc-0123456789ab";
        var page = "<html><head><title>Màn chi tiết</title></head><body><h3>Chi tiết tủ</h3></body></html>";
        var template = $"<html><body><h1>Khung</h1><iframe src=\"about:blank#{pageId}\"></iframe></body></html>";
        var manifest = new Dictionary<string, object>
        {
            [pageId] = new { mime = "text/html", compressed = true, data = Convert.ToBase64String(Gzip(Encoding.UTF8.GetBytes(page))) },
        };
        var html = $"""
            <html><body>
              <script type="__bundler/manifest">{JsonSerializer.Serialize(manifest)}</script>
              <script type="__bundler/page_order">["{pageId}"]</script>
              <script type="__bundler/template">{JsonSerializer.Serialize(template)}</script>
            </body></html>
            """;

        var text = HtmlDocumentTextExtractor.Extract(Encoding.UTF8.GetBytes(html));

        Assert.NotNull(text);
        Assert.Contains("# Khung", text);
        Assert.Contains("--- Khung nhúng: Màn chi tiết ---", text);
        Assert.Contains("### Chi tiết tủ", text);
    }

    [Fact]
    public void Extract_DataUriImages_KeepsScreenshots_SkipsIconsAndDuplicates()
    {
        var big = Convert.ToBase64String(PngHeader(640, 480));
        var icon = Convert.ToBase64String(PngHeader(16, 16));
        var html = $"""
            <html><body>
              <p>Màn hình cũ</p>
              <img src="data:image/png;base64,{big}">
              <img src="data:image/png;base64,{icon}">
              <p>Lặp lại</p>
              <img src="data:image/png;base64,{big}">
            </body></html>
            """;

        var result = HtmlDocumentTextExtractor.Extract(Encoding.UTF8.GetBytes(html), _dir);

        Assert.Equal(1, result.ImageCount);
        Assert.True(File.Exists(Path.Combine(_dir, "figure-1.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "figure-2.png")));
        // Mốc đứng NGAY SAU đoạn chữ trước hình, để model ghép hình với đúng chỗ của nó.
        Assert.Contains("Màn hình cũ\n[Hình 1", result.Text);
    }

    [Fact]
    public void Extract_DeeplyNestedMarkup_DoesNotBlowTheStack()
    {
        // File do người dùng upload: một chuỗi thẻ lồng rất sâu không được làm tràn stack (StackOverflow
        // không bắt được — nó giết cả tiến trình web).
        var html = "<p>Trước</p>" + string.Concat(Enumerable.Repeat("<span>", 50_000)) + "sâu";

        var text = HtmlDocumentTextExtractor.Extract(Encoding.UTF8.GetBytes(html));

        Assert.NotNull(text);
        Assert.StartsWith("Trước", text);
    }

    [Fact]
    public void Extract_PathologicalNesting_GivesUpWithinTheParseBudget()
    {
        // Thẻ khối lồng sâu làm bộ dựng cây HTML5 chạy bậc hai (100k "<div>" là cả phút CPU). Hết ngân sách
        // thì bỏ phần bóc chữ — không được giữ luồng request tới khi parse xong.
        var html = "<p>Trước</p>" + string.Concat(Enumerable.Repeat("<div>", 100_000)) + "sâu";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = HtmlDocumentTextExtractor.Extract(
            Encoding.UTF8.GetBytes(html), imageTargetDir: null, TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Parse chạy {watch.Elapsed} — ngân sách không có hiệu lực.");
    }

    [Fact]
    public void Extract_CallerCancellation_IsNotSwallowed()
    {
        // Người dùng huỷ request khác với "file không đọc được": phải nổi lên như mọi đường ingest khác.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            HtmlDocumentTextExtractor.Extract(Encoding.UTF8.GetBytes("<h1>Mockup</h1>"), null, cts.Token));
    }

    [Fact]
    public void Extract_Garbage_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(HtmlDocumentTextExtractor.Extract(Array.Empty<byte>()));
        Assert.Null(HtmlDocumentTextExtractor.Extract(Encoding.UTF8.GetBytes("<html><body>   </body></html>")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Chỉ header PNG (chữ ký + IHDR): đủ để bộ bóc đọc kích thước pixel, không cần ảnh giải mã được.
    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }
            .CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), (uint)height);
        bytes[24] = 8;
        bytes[25] = 2;
        bytes[40] = (byte)(width % 251); // khác nhau theo kích thước để hai ảnh khác nhau không bị coi là trùng
        return bytes;
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }
}
