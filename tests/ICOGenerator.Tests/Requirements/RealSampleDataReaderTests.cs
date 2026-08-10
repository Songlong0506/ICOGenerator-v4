using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// "Dữ liệu mẫu THẬT" đi tới HAI đầu: prompt sinh AI Design Spec (để POC seed bằng đúng danh mục của đơn
// vị yêu cầu) và PocSampleDataCheck (rút token đặc trưng ra để kiểm POC có thật sự dùng dữ liệu đó không).
// Cả hai đều cần BẢN GHI, không cần thống kê.
//
// Đây là chỗ hai consumer của cùng một chuỗi text muốn hai nửa ngược nhau: SourceContextBuilder gửi text
// cho BA đọc nên cần khối "Thống kê cột" đứng TRƯỚC để nó sống sót qua trần 20.000 ký tự; còn ở đây trần
// chỉ 3.000 ký tự, nên một bảng nhiều cột có phần thống kê ăn gần hết ngân sách sẽ đẩy hết bản ghi ra
// ngoài — và thứ tới POC là mấy dòng "có giá trị 262/262 · ĐỦ 5 giá trị". Khi đó token đặc trưng rút ra
// được là từ vựng thống kê chứ không phải danh mục thật, tức POC seed sai mà cổng kiểm cũng mù theo.
public class RealSampleDataReaderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public RealSampleDataReaderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "Kế hoạch đào tạo" });
        db.SaveChanges();
    }

    [Fact]
    public async Task ReadAsync_KeepsTheDataRows_AndDropsTheColumnStatsBlock()
    {
        await AddSpreadsheetAsync($"""
            ### Sheet: Sheet1
            Tổng: 262 dòng dữ liệu, 3 cột.

            {SpreadsheetTextExtractor.ColumnStatsHeading} (trên 262 dòng)
            - Item Type: có giá trị 257/262 · ĐỦ 2 giá trị: WBT (114), COURSE (91)
            - Item Title: có giá trị 257/262 · 136 giá trị phân biệt

            {SpreadsheetTextExtractor.DataRowsHeading} (29 dòng đầu làm mẫu — chỉ để thấy hình dạng dữ liệu)
            Global ID | Item Type | Item Title
            11054396 | COURSE | [QM-QM001] Quality at Bosch-B
            """);

        var text = await RealSampleDataReader.ReadAsync(NewDb(), _projectId);

        Assert.NotNull(text);
        // Bản ghi thật — thứ POC seed lên màn hình và PocSampleDataCheck rút token từ đó.
        Assert.Contains("[QM-QM001] Quality at Bosch-B", text);
        Assert.Contains("Global ID | Item Type | Item Title", text);
        // Thống kê là chỉ dẫn cho BA đọc file, không phải dữ liệu mẫu — không được ăn vào trần 3.000 ký tự.
        Assert.DoesNotContain("có giá trị 257/262", text);
        Assert.DoesNotContain(SpreadsheetTextExtractor.ColumnStatsHeading, text);
    }

    // Text từ Word (biểu mẫu render "ô | ô") và bảng tính bóc bởi phiên bản trước không có mốc nào — phải
    // giữ nguyên toàn bộ thay vì trả về rỗng.
    [Fact]
    public async Task ReadAsync_KeepsEverythingWhenThereIsNoDataRowsMarker()
    {
        await AddSpreadsheetAsync("Mã vật tư | Tên | Đơn vị\nVT-001 | Vòng bi 6205 | Cái");

        var text = await RealSampleDataReader.ReadAsync(NewDb(), _projectId);

        Assert.NotNull(text);
        Assert.Contains("Vòng bi 6205", text);
    }

    private async Task AddSpreadsheetAsync(string extractedText)
    {
        await using var db = NewDb();
        db.ProjectSourceFiles.Add(new ProjectSourceFile
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            FileName = "KeHoach.xlsx",
            StoredPath = "/tmp/KeHoach.xlsx",
            Kind = SourceFileKind.Spreadsheet,
            ExtractedText = extractedText
        });
        await db.SaveChangesAsync();
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
