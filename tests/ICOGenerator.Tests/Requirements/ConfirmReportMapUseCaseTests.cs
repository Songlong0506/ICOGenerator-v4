using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Đường GỬI của bảng báo cáo. Việc thật sự của nó không phải là ghi một cột JSON mà là GIEO MÀN HÌNH VÀO
// PHẠM VI: một báo cáo là một chỗ người dùng mở ra và nhìn thấy, nằm lại trong cột ReportMap thì nó không có
// DÒNG nào ở bảng phân quyền và không có mục nào ở "## 6. Screens To Generate" — mặc nhiên "không ai được
// xem" một màn hình người dùng vừa đặt hàng. Đó cũng là lý do bảng này đứng TRƯỚC bảng phân quyền.
public class ConfirmReportMapUseCaseTests : IDisposable
{
    private const string ConfirmedEntities = """
        [{"entity":"Kế hoạch đào tạo","description":"Kế hoạch lớp học theo quý",
          "fields":[{"name":"Quý","meaning":"Quý áp dụng","used":true}],"included":true}]
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public ConfirmReportMapUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Đào tạo",
            EntityMap = ConfirmedEntities,
            // Phạm vi đã chắt từ hội thoại, chưa ai rà.
            ScreenScopeMap = """[{"screen":"Màn hình Training Plan","included":true}]"""
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_StoresWhatTheUserChose_AndComposesTheChatMessage()
    {
        var result = await ExecuteAsync("""
            [{"report":"Training Progress Report","question":"để biết đơn vị nào chưa đạt",
              "source":"Kế hoạch đào tạo","breakdown":"quý; đơn vị","included":true},
             {"report":"Training Cost Report","question":"","source":"","breakdown":"","included":false}]
            """);

        Assert.Equal(2, result.Rows);

        var stored = ReportMapBuilder.Parse(await LoadAsync(p => p.ReportMap));
        Assert.True(stored.Single(r => r.Report == "Training Progress Report").Included);
        Assert.False(stored.Single(r => r.Report == "Training Cost Report").Included);

        Assert.Contains("lấy số từ: Kế hoạch đào tạo", result.Message);
        Assert.Contains("(bỏ: Training Cost Report)", result.Message);
    }

    // GIEO MÀN HÌNH — xem ghi chú đầu file. CHỈ THÊM, không đụng tới dòng nào đang có: bảng màn hình có thể
    // đã được người dùng tự tay rà, ghi đè nó bằng mấy dòng báo cáo là xoá sạch phạm vi đã duyệt.
    [Fact]
    public async Task ExecuteAsync_SeedsTheKeptReportsIntoTheScreenTable_WithoutLosingWhatWasThere()
    {
        await ExecuteAsync("""
            [{"report":"Training Progress Report","source":"Kế hoạch đào tạo","included":true},
             {"report":"Training Cost Report","included":false}]
            """);

        var scope = ScreenScopeMapBuilder.EffectiveScreens(await LoadAsync(p => p.ScreenScopeMap));
        Assert.Equal(new[] { "Màn hình Training Plan", "Training Progress Report" }, scope);
    }

    // Chốt lần thứ hai (người dùng sửa lại bảng ở một lượt sau) không được đẻ ra dòng trùng trong phạm vi —
    // mỗi dòng trùng là một màn hình nữa phải rà ở bảng màn hình.
    [Fact]
    public async Task ExecuteAsync_DoesNotDuplicateAScopeItemItAlreadySeeded()
    {
        const string json = """[{"report":"Training Progress Report","included":true}]""";
        await ExecuteAsync(json);
        await ExecuteAsync(json);

        var scope = ScreenScopeMapBuilder.EffectiveScreens(await LoadAsync(p => p.ScreenScopeMap));
        Assert.Single(scope, s => s == "Training Progress Report");
    }

    // Người dùng bỏ tích HẾT là một câu trả lời hợp lệ ("ứng dụng không cần báo cáo nào"), nên bảng vẫn phải
    // được LƯU: không lưu thì cổng mở lại và họ bị bày đúng cái bảng vừa tắt ở lượt sau.
    [Fact]
    public async Task ExecuteAsync_StoresTheTable_EvenWhenTheUserDroppedEveryReport()
    {
        var result = await ExecuteAsync("""[{"report":"Training Progress Report","included":false}]""");

        Assert.Equal(1, result.Rows);
        Assert.Contains("không cần báo cáo", result.Message);
        Assert.False(ReportMapGate.ShouldAsk(
            "- Báo cáo / thống kê: [RÕ] Không cần.",
            await LoadAsync(p => p.ReportMap),
            ConfirmedEntities));
    }

    // Nguồn không khớp đối tượng nào bị XOÁ khỏi ô, và bộ đối chiếu đọc lại từ DB chứ không tin payload:
    // bảng có thể đã nằm trên màn hình từ trước lượt người dùng chốt lại bảng đối tượng.
    [Fact]
    public async Task ExecuteAsync_ClearsASourceThatNoConfirmedEntityMatches()
    {
        await ExecuteAsync("""[{"report":"Training Cost Report","source":"Bảng lương","included":true}]""");

        Assert.Equal(string.Empty, ReportMapBuilder.Parse(await LoadAsync(p => p.ReportMap)).Single().Source);
    }

    private async Task<ConfirmReportMapUseCase.Result> ExecuteAsync(string reportsJson)
    {
        await using var db = NewDb();
        return await new ConfirmReportMapUseCase(db).ExecuteAsync(_projectId, reportsJson);
    }

    private async Task<string?> LoadAsync(Func<Project, string?> pick)
    {
        await using var db = NewDb();
        return pick(await db.Projects.FirstAsync(p => p.Id == _projectId));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
