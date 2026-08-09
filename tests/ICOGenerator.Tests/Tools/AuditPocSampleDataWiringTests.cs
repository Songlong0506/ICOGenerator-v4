using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Tools;

// Sợi dây "dữ liệu mẫu THẬT" chạy qua hai đầu: RealSampleDataReader đọc text đã bóc từ Excel/Word của
// project (đầu nạp vào prompt sinh spec), rồi AgentTaskWorker đưa CHÍNH text đó cho WorkspaceTools làm
// chuẩn đối chiếu của AuditPocContent. Test này giữ sợi dây: hai đầu lệch nhau thì cổng kiểm sẽ chấm POC
// theo một tập dữ liệu khác với tập đã đưa cho model — tệ hơn là không kiểm.
public class AuditPocSampleDataWiringTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ico-audit-" + Guid.NewGuid().ToString("N")[..8]);

    public AuditPocSampleDataWiringTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.ProjectSourceFiles.Add(new ProjectSourceFile
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            FileName = "danh-muc.xlsx",
            Kind = SourceFileKind.Spreadsheet,
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ExtractedText = "Mã vật tư | Tên vật tư\nVT-0091 | Vòng bi trục khuỷu SKF\nVT-0092 | Dầu thủy lực Shell Tellus\nVT-0093 | Gioăng làm kín Viton"
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ReadAsync_ReturnsTheExtractedTableText_ForSpreadsheetAndWordSourcesOnly()
    {
        await using var db = NewDb();
        db.ProjectSourceFiles.Add(new ProjectSourceFile
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            FileName = "quy-trinh.pdf",
            Kind = SourceFileKind.Pdf,
            ContentType = "application/pdf",
            ExtractedText = "Văn xuôi mô tả quy trình duyệt đơn"
        });
        await db.SaveChangesAsync();

        var sample = await RealSampleDataReader.ReadAsync(db, _projectId);

        Assert.NotNull(sample);
        Assert.Contains("Vòng bi trục khuỷu SKF", sample);
        // Text bóc từ PDF là văn xuôi — đưa vào chỗ "dữ liệu mẫu" chỉ làm nhiễu, không thành bản ghi seed.
        Assert.DoesNotContain("Văn xuôi mô tả quy trình", sample);
    }

    [Fact]
    public async Task AuditPocContent_WhenTheDemoIgnoresTheAttachedCatalogue_ReportsItAsAnIssue()
    {
        await using var db = NewDb();
        var tools = NewTools(db);
        WritePoc("<table><tr><td>Sản phẩm A</td><td>Nguyễn Văn B</td></tr></table>");

        tools.SetPocRealSampleData(await RealSampleDataReader.ReadAsync(db, _projectId));

        var report = await tools.AuditPocContent();

        Assert.Contains("KHÔNG dùng gì từ tài liệu", report);
        Assert.Contains("Vòng bi trục khuỷu SKF", report);
        // Phải nằm trong khối ISSUES (thứ agent bắt buộc sửa), không phải một dòng ghi chú cuối báo cáo.
        Assert.Contains("ISSUES (fix these before returning your final result)", report);
    }

    [Fact]
    public async Task AuditPocContent_WhenTheDemoUsesTheCatalogue_SaysNothingAboutSampleData()
    {
        await using var db = NewDb();
        var tools = NewTools(db);
        WritePoc("""
            <table>
              <tr><td>Vòng bi trục khuỷu SKF</td></tr>
              <tr><td>Dầu thủy lực Shell Tellus</td></tr>
              <tr><td>Gioăng làm kín Viton</td></tr>
            </table>
            """);

        tools.SetPocRealSampleData(await RealSampleDataReader.ReadAsync(db, _projectId));

        var report = await tools.AuditPocContent();

        Assert.DoesNotContain("KHÔNG dùng gì từ tài liệu", report);
        Assert.DoesNotContain("tên bịa", report);
    }

    // Project KHÔNG đính kèm tài liệu nào: không có gì để đối chiếu, nên phép kiểm "dùng dữ liệu thật"
    // phải im lặng tuyệt đối — báo agent đi tìm một tài liệu không tồn tại là gửi nó vào ngõ cụt. Tên
    // placeholder thì vẫn đáng nhắc (nó xấu bất kể có tài liệu hay không), nhưng chỉ ở mức WARNING.
    [Fact]
    public async Task AuditPocContent_WithoutAnyAttachedDocument_OnlyWarnsAboutPlaceholders()
    {
        await using var db = NewDb();
        var tools = NewTools(db);
        WritePoc("<table><tr><td>Sản phẩm A</td><td>Nguyễn Văn B</td></tr></table>");

        // Không gọi SetPocRealSampleData — đúng như một project chưa upload gì.
        var report = await tools.AuditPocContent();

        Assert.DoesNotContain("KHÔNG dùng gì từ tài liệu", report);
        Assert.DoesNotContain("tên bịa", report);
        Assert.Contains("dùng tên placeholder", report);
        Assert.Contains("WARNINGS (fix if unintended)", report);
    }

    private void WritePoc(string content)
    {
        var dir = Path.Combine(_root, "p", "04_Implementation");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "poc-demo.html"),
            "<html><body><nav class=\"sidebar-nav\"></nav>"
            + PocTemplate.StartMarker
            + "<section class=\"page-view active\" data-view=\"Kho vật tư\">" + content + "</section>"
            + PocTemplate.EndMarker
            + "</body></html>");
    }

    private WorkspaceTools NewTools(AppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = _root,
                // Không có Chromium trong unit test — tắt hẳn để audit chỉ chạy phần tĩnh.
                ["Poc:RuntimeCheck:Enabled"] = "false"
            })
            .Build();

        var resolver = new WorkspacePathResolver(configuration);
        var visual = new PocVisualReviewer(db, new UnusedLlm(), new StubPrompts(), configuration,
            NullLogger<PocVisualReviewer>.Instance);
        var tools = new WorkspaceTools(configuration, resolver,
            new PlaywrightPocRuntimeChecker(configuration, NullLogger<PlaywrightPocRuntimeChecker>.Instance), visual);
        tools.SetWorkspace("p");
        return tools;
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class UnusedLlm : ILlmClient
    {
        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<Microsoft.Extensions.AI.ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<Microsoft.Extensions.AI.ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => throw new NotSupportedException();
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
