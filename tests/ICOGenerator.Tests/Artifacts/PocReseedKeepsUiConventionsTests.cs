using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Chốt chặn cho đúng lỗ hổng đã sinh ra bộ quy ước trình bày.
//
// Vòng dựng POC MỚI (task PocPreview không có RevisionFeedback) chạy AgentTaskWorker.EnsureDesignAssetsAsync:
// nó ghi đè poc-demo.html về shell template rỗng và xoá cả poc-history/ + kết quả tự kiểm. Đó là hành vi
// CỐ Ý — POC dựng lại thì bản chụp và báo cáo của bản cũ không còn để so. Nhưng nó cũng là lý do mọi góp ý
// giao diện đã được vá vào HTML ở đường "Nhờ đội Dev chỉnh bản demo" từng mất trắng.
//
// Bộ quy ước sống được CHỈ VÌ nó nằm ngoài tập bị dọn. Test này khoá bất biến đó lại: ai thêm một bước
// "dọn sạch 04_Implementation" vào vòng seed sẽ làm test đỏ thay vì âm thầm khôi phục lỗ hổng.
public class PocReseedKeepsUiConventionsTests : IDisposable
{
    private readonly string _workspace;

    public PocReseedKeepsUiConventionsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "ico-poc-reseed-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_workspace, "04_Implementation"));
    }

    [Fact]
    public void ReseedingThePocDemo_WipesTheBuildButKeepsTheUiConventions()
    {
        var demoPath = Path.Combine(_workspace, PocTemplate.MockupRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var conventionsPath = Path.Combine(_workspace, "04_Implementation", PocUiConventionService.FileName);

        File.WriteAllText(demoPath, "<html><!-- POC_CONTENT_START -->bản demo đã sửa theo góp ý<!-- POC_CONTENT_END --></html>");
        File.WriteAllText(conventionsPath, """{"conventions":[{"id":"UI-1","text":"Nút xác nhận ghi là \"Gửi duyệt\"."}]}""");
        PocSnapshots.TryCapture(_workspace, demoPath);

        // Đúng ba việc EnsureDesignAssetsAsync làm khi dựng lại POC từ đầu.
        var template = File.ReadAllText(TemplatePath());
        File.WriteAllText(demoPath, PocTemplate.StripDeveloperGuide(PocTemplate.SeedFromTemplate(template)!));
        PocVerification.Reset(_workspace);
        PocSnapshots.Reset(_workspace);

        // Bản demo đã về template rỗng và lịch sử đã bị dọn — hành vi cố ý, không đụng tới.
        Assert.Contains(PocTemplate.Placeholder, File.ReadAllText(demoPath));
        Assert.DoesNotContain("bản demo đã sửa theo góp ý", File.ReadAllText(demoPath));
        Assert.False(Directory.Exists(PocSnapshots.GetFolderPath(_workspace)));

        // …còn quy ước trình bày thì phải sống sót, nếu không người dùng gặp lại lỗi họ đã góp ý.
        Assert.True(File.Exists(conventionsPath));
        Assert.Contains("Gửi duyệt", File.ReadAllText(conventionsPath));
    }

    // Cùng cách tìm Prompts/ như PromptConventionTests: ưu tiên bản copy trong bin, không có thì đi ngược
    // lên repo root.
    private static string TemplatePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Prompts", "Design", "poc-template.html");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Không tìm thấy Prompts/Design/poc-template.html từ " + AppContext.BaseDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* thư mục tạm; dọn được thì dọn */ }
    }
}
