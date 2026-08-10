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

    // BẢNG CỘT đã chốt ⇒ dữ liệu mẫu đi tới POC chỉ còn các cột người dùng thật sự dùng. Đây là chỗ bảng
    // cột trả tiền cho chính nó: người dùng bỏ tích "Revision Number" mà vẫn thấy nó nằm như một trường
    // trên bản demo thì mất niềm tin y hệt như khi không hỏi gì — chỉ đi bằng cửa sau.
    [Fact]
    public async Task ReadAsync_KeepsOnlyTheColumnsTheUserConfirmed()
    {
        await AddSpreadsheetAsync($"""
            {SpreadsheetTextExtractor.DataRowsHeading} (2 dòng đầu làm mẫu)
            Global ID | Item Title | Revision Number
            11054396 | Quality at Bosch-B | 1
            11227524 | Compliance - Antitrust Law-A | 3
            """,
            """
            [{"FileName":"KeHoach.xlsx","Column":"Global ID","Meaning":"mã nhân viên","Used":true},
             {"FileName":"KeHoach.xlsx","Column":"Item Title","Meaning":"tên lớp học","Used":true},
             {"FileName":"KeHoach.xlsx","Column":"Revision Number","Meaning":"phiên bản nội dung","Used":false}]
            """);

        var text = await RealSampleDataReader.ReadAsync(NewDb(), _projectId);

        Assert.NotNull(text);
        Assert.Contains("Global ID | Item Title", text);
        Assert.Contains("11054396 | Quality at Bosch-B", text);
        // Cột của hệ cũ biến mất khỏi cả hàng tiêu đề lẫn các dòng dữ liệu.
        Assert.DoesNotContain("Revision Number", text);
        Assert.DoesNotContain("| 3", text);
    }

    // Chưa chốt bảng nào (file cũ, hoặc người dùng chưa gửi bảng) ⇒ giữ nguyên hành vi trước đây. Cắt bớt
    // dữ liệu mẫu vì một bảng chưa ai duyệt là tự ý thu hẹp thứ POC được phép seed.
    [Fact]
    public async Task ReadAsync_KeepsEveryColumn_WhenTheColumnMapIsNotConfirmedYet()
    {
        await AddSpreadsheetAsync($"""
            {SpreadsheetTextExtractor.DataRowsHeading} (1 dòng đầu làm mẫu)
            Global ID | Item Title | Revision Number
            11054396 | Quality at Bosch-B | 1
            """, columnMap: null);

        var text = await RealSampleDataReader.ReadAsync(NewDb(), _projectId);

        Assert.Contains("Revision Number", text!);
    }

    // Bảng cột không khớp hàng tiêu đề nào (file đã bị thay bằng bản khác) ⇒ giữ nguyên. Cắt sạch dữ liệu
    // mẫu tệ hơn nhiều so với để lọt vài cột thừa: POC mất hẳn danh mục thật để seed.
    [Fact]
    public async Task ReadAsync_KeepsEveryColumn_WhenNoConfirmedColumnMatchesTheHeader()
    {
        await AddSpreadsheetAsync($"""
            {SpreadsheetTextExtractor.DataRowsHeading} (1 dòng đầu làm mẫu)
            Global ID | Item Title
            11054396 | Quality at Bosch-B
            """,
            """[{"Column":"Ma phong","Used":true}]""");

        var text = await RealSampleDataReader.ReadAsync(NewDb(), _projectId);

        Assert.Contains("Global ID | Item Title", text!);
        Assert.Contains("Quality at Bosch-B", text);
    }

    private async Task AddSpreadsheetAsync(string extractedText, string? columnMap = null)
    {
        await using var db = NewDb();
        db.ProjectSourceFiles.Add(new ProjectSourceFile
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            FileName = "KeHoach.xlsx",
            StoredPath = "/tmp/KeHoach.xlsx",
            Kind = SourceFileKind.Spreadsheet,
            ExtractedText = extractedText,
            ColumnMap = columnMap
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
