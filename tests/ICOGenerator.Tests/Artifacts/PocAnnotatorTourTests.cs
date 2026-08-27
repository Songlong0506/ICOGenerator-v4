using Microsoft.Playwright;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

/// <summary>
/// Tính năng "chỉ chỗ" của trang POC Review, chạy trong Chromium THẬT với chính file
/// <c>wwwroot/js/poc-annotator.js</c> — không có bản mô phỏng logic nào ở đây, vì thứ đáng kiểm chính là
/// mã sẽ chạy trong iframe của người dùng.
///
/// <para>
/// Vì sao đáng một test trình duyệt: bản đầu tiên của tính năng này ĐOÁN phần tử bằng cách so từ của câu
/// bước với chữ trên các nút/ô bảng, và nó khoanh nhầm thường xuyên tới mức người dùng đề nghị bỏ hẳn
/// tính năng. Nay phần tử do agent dựng POC khai báo sẵn bằng <c>data-uat</c>; các test dưới đây chốt
/// đúng hai điều làm nên khác biệt đó — tô sáng ĐÚNG phần tử mang mã neo (kể cả khi có mồi nhử cùng chữ),
/// và khi không tra ra thì NÓI RA bằng một trạng thái cụ thể chứ không nháy đại cho có phản hồi.
/// </para>
///
/// <para>
/// Không có Chromium (máy dev thường) ⇒ test tự bỏ qua, giống <see cref="PocRuntimeCheckerTests"/>.
/// Annotator vốn nói chuyện với trang cha qua <c>window.parent</c>; ở trang top-level thì
/// <c>window.parent === window</c> nên vẫn gửi/nhận được chính các message ấy, không cần dựng iframe.
/// </para>
/// </summary>
public class PocAnnotatorTourTests : IAsyncLifetime
{
    private static readonly string? BrowserPath = FindBrowser();

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    private static string? FindBrowser()
    {
        var env = Environment.GetEnvironmentVariable("POC_BROWSER_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;
        const string claudeWebChromium = "/opt/pw-browsers/chromium";
        return File.Exists(claudeWebChromium) ? claudeWebChromium : null;
    }

    private static string AnnotatorPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot", "js", "poc-annotator.js");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("Không tìm thấy wwwroot/js/poc-annotator.js từ " + AppContext.BaseDirectory);
    }

    public async Task InitializeAsync()
    {
        if (BrowserPath == null)
            return;
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = BrowserPath,
            Headless = true
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser != null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    // Một POC hai màn hình: màn "Đơn" có nút thật mang neo 1.2 và một MỒI NHỬ cùng chữ "Gửi" đứng trước
    // nó trong DOM — thứ mà lượt đoán theo chữ ngày trước sẽ chọn.
    private const string Poc = """
        <!doctype html><html><head><meta charset="utf-8"></head><body>
        <section class="page-view active" data-view="Trang chủ">
          <h1>Trang chủ</h1>
          <button id="decoy">Gửi</button>
        </section>
        <section class="page-view" data-view="Đơn">
          <button id="real" data-uat="1.2">Gửi</button>
          <span id="status" data-uat="1.3">Chờ duyệt</span>
          <span id="late" data-uat="1.4" style="display:none">Đã duyệt</span>
        </section>
        <script>
        window.pocNavigate = function (label) {
            document.querySelectorAll('section.page-view').forEach(function (s) {
                s.classList.toggle('active', (s.dataset.view || '') === label);
            });
        };
        window.__tour = [];
        window.addEventListener('message', function (e) {
            if (e.data && e.data.type === 'poc-tour-result') window.__tour.push(e.data);
        });
        </script>
        </body></html>
        """;

    private async Task<IPage> OpenAsync(string html)
    {
        var page = await _browser!.NewPageAsync();
        await page.SetContentAsync(html);
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Path = AnnotatorPath() });
        return page;
    }

    private static async Task<string> TourAsync(IPage page, string screen, string anchor)
    {
        await page.EvaluateAsync(
            "(arg) => { window.__tour = []; window.postMessage({ type: 'poc-tour-step', screen: arg.screen, anchor: arg.anchor }, '*'); }",
            new { screen, anchor });
        await page.WaitForTimeoutAsync(500);
        return await page.EvaluateAsync<string>("() => (window.__tour[0] || {}).status || ''");
    }

    [Fact]
    public async Task Anchor_HighlightsTheDeclaredElement_NotTheDecoyWithTheSameText()
    {
        if (BrowserPath == null) return;
        var page = await OpenAsync(Poc);

        var status = await TourAsync(page, "Đơn", "1.2");

        Assert.Equal("ok", status);
        Assert.Equal(new[] { "real" }, await page.EvaluateAsync<string[]>(
            "() => Array.from(document.querySelectorAll('.poc-tour-target')).map(x => x.id)"));
        // Mở đúng màn hình của kịch bản trước khi tô sáng — phần tử nằm ở màn khác vẫn tới được.
        Assert.Equal("Đơn", await page.EvaluateAsync<string>(
            "() => document.querySelector('.page-view.active').dataset.view"));
    }

    // Bước KIỂM TRA neo vào chỗ hiển thị kết quả: vẫn chỉ chỗ được, đó là lý do quy ước cấm bỏ trống neo
    // của bước kiểm tra.
    [Fact]
    public async Task Anchor_OnAReadOnlyElement_IsHighlightedToo()
    {
        if (BrowserPath == null) return;
        var page = await OpenAsync(Poc);

        Assert.Equal("ok", await TourAsync(page, "Đơn", "1.3"));
        Assert.Equal(new[] { "status" }, await page.EvaluateAsync<string[]>(
            "() => Array.from(document.querySelectorAll('.poc-tour-target')).map(x => x.id)"));
    }

    // Ba cách TRƯỢT, ba câu trả lời khác nhau — vì việc người dùng phải làm tiếp trong ba tình huống này
    // khác hẳn nhau. Điểm chung: KHÔNG tô sáng gì cả, thà không chỉ còn hơn chỉ sai.
    [Fact]
    public async Task StepWithoutAnchor_ReportsMissing_AndHighlightsNothing()
    {
        if (BrowserPath == null) return;
        var page = await OpenAsync(Poc);

        Assert.Equal("missing", await TourAsync(page, "Đơn", "2.1"));
        Assert.Equal(0, await page.EvaluateAsync<int>("() => document.querySelectorAll('.poc-tour-target').length"));
    }

    [Fact]
    public async Task HiddenAnchor_ReportsHidden_SoTheUserKnowsToDoTheEarlierSteps()
    {
        if (BrowserPath == null) return;
        var page = await OpenAsync(Poc);

        Assert.Equal("hidden", await TourAsync(page, "Đơn", "1.4"));
        Assert.Equal(0, await page.EvaluateAsync<int>("() => document.querySelectorAll('.poc-tour-target').length"));
    }

    [Fact]
    public async Task PocBuiltBeforeAnchorsExisted_ReportsUnsupported()
    {
        if (BrowserPath == null) return;
        var page = await OpenAsync("""
            <!doctype html><html><head><meta charset="utf-8"></head><body>
            <section class="page-view active" data-view="Trang chủ"><button>Gửi</button></section>
            <script>
            window.__tour = [];
            window.addEventListener('message', function (e) {
                if (e.data && e.data.type === 'poc-tour-result') window.__tour.push(e.data);
            });
            </script>
            </body></html>
            """);

        Assert.Equal("unsupported", await TourAsync(page, "", "1.1"));
    }

    // Mã neo tới từ postMessage nên nó là dữ liệu ngoài: ghép thẳng vào selector là mở một đường chèn
    // selector. Mã sai định dạng phải bị chặn TRƯỚC khi thành querySelector, không phải ném lỗi.
    [Fact]
    public async Task MalformedAnchor_IsRejectedInsteadOfBeingSpliceInto_QuerySelector()
    {
        if (BrowserPath == null) return;
        var page = await OpenAsync(Poc);

        Assert.Equal("missing", await TourAsync(page, "Đơn", "1.2\"], [id=\"decoy"));
        Assert.Equal(0, await page.EvaluateAsync<int>("() => document.querySelectorAll('.poc-tour-target').length"));
    }
}
