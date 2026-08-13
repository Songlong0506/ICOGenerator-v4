using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bảng cột người dùng vừa tích được lưu vào chính file nguồn — từ đó SourceContextBuilder và
// RealSampleDataReader đọc ra. Vì payload tới TỪ TRÌNH DUYỆT nên use case này không được tin nó, kể cả
// khi chính server vừa render bảng đó ra vài giây trước: một dòng bịa lọt vào đây là POC seed theo một
// cột không tồn tại, và không tầng nào phía sau còn phân biệt được.
public class ConfirmSourceColumnMapUseCaseTests : IDisposable
{
    private const string ExtractedText = """
        ### Sheet: Sheet1
        Tổng: 262 dòng dữ liệu, 3 cột.

        #### Thống kê cột (trên 262 dòng)
        - Global ID: có giá trị 262/262 · 13 giá trị phân biệt
        - Item Title: có giá trị 257/262 · 136 giá trị phân biệt
        - Revision Number: có giá trị 262/262 · ĐỦ 3 giá trị: 1 (218), 3 (21), 2 (18)

        #### Dòng dữ liệu (1 dòng đầu làm mẫu)
        Global ID | Item Title | Revision Number
        11054396 | Quality at Bosch-B | 1
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _sourceId = Guid.NewGuid();

    public ConfirmSourceColumnMapUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "Kế hoạch đào tạo" });
        db.ProjectSourceFiles.Add(new ProjectSourceFile
        {
            Id = _sourceId,
            ProjectId = _projectId,
            FileName = "KeHoach.xlsx",
            StoredPath = "/tmp/KeHoach.xlsx",
            Kind = SourceFileKind.Spreadsheet,
            ExtractedText = ExtractedText
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_StoresWhatTheUserTicked_OnTheSourceFile()
    {
        var updated = await ExecuteAsync("""
            [{"fileName":"KeHoach.xlsx","column":"Global ID","meaning":"mã số nhân viên","used":true},
             {"fileName":"KeHoach.xlsx","column":"Item Title","meaning":"tên lớp học","used":true},
             {"fileName":"KeHoach.xlsx","column":"Revision Number","meaning":"phiên bản nội dung","used":false}]
            """);

        Assert.Equal(1, updated);

        var stored = SourceColumnMapBuilder.Parse(await LoadColumnMapAsync());
        Assert.Equal(new[] { "Global ID", "Item Title" }, stored.Where(c => c.Used).Select(c => c.Column));
        Assert.Equal("mã số nhân viên", stored.Single(c => c.Column == "Global ID").Meaning);
        // Cột bỏ tích vẫn được LƯU (không phải bị xoá): nó là vế "đây là dữ liệu hệ cũ" mà BA cần biết để
        // thôi hỏi lại, và người dùng cần thấy để đổi ý.
        Assert.False(stored.Single(c => c.Column == "Revision Number").Used);
    }

    [Fact]
    public async Task ExecuteAsync_DropsColumnsThatDoNotExistInTheFile()
    {
        await ExecuteAsync("""
            [{"fileName":"KeHoach.xlsx","column":"Global ID","meaning":"mã nhân viên","used":true},
             {"fileName":"KeHoach.xlsx","column":"Sức chứa phòng","meaning":"bịa","used":true}]
            """);

        var stored = SourceColumnMapBuilder.Parse(await LoadColumnMapAsync());
        Assert.DoesNotContain(stored, c => c.Column == "Sức chứa phòng");
        Assert.Equal(3, stored.Count); // vẫn phủ đủ 3 cột thật của file
    }

    // Payload rỗng/hỏng: không ghi gì và báo về 0 để UI giữ bảng lại. Ghi một bảng rỗng đè lên là mất
    // đường chốt của file mà chẳng ai biết.
    [Fact]
    public async Task ExecuteAsync_StoresNothing_WhenThePayloadIsEmptyOrBroken()
    {
        Assert.Equal(0, await ExecuteAsync("[]"));
        Assert.Equal(0, await ExecuteAsync("{ hỏng"));
        Assert.Equal(0, await ExecuteAsync(null));
        Assert.Null(await LoadColumnMapAsync());
    }

    // Trình duyệt gửi thiếu tên file (dữ liệu cũ trên màn hình, hoặc model để trống ở lượt trước): chỉ
    // nhận khi project có ĐÚNG một bảng tính. Gán bừa vào một file trong nhiều file là chốt phạm vi cột
    // cho nhầm bảng — hỏng câm lặng, và người dùng không có cách nào nhìn ra.
    [Fact]
    public async Task ExecuteAsync_AcceptsAMissingFileName_OnlyWhenThereIsExactlyOneSpreadsheet()
    {
        Assert.Equal(1, await ExecuteAsync("""[{"column":"Global ID","meaning":"mã nhân viên","used":true}]"""));

        await using (var db = NewDb())
        {
            db.ProjectSourceFiles.Add(new ProjectSourceFile
            {
                Id = Guid.NewGuid(),
                ProjectId = _projectId,
                FileName = "Phong.xlsx",
                StoredPath = "/tmp/Phong.xlsx",
                Kind = SourceFileKind.Spreadsheet,
                ExtractedText = ExtractedText
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await ExecuteAsync("""[{"column":"Item Title","meaning":"tên lớp","used":true}]"""));
    }

    // Tin nhắn gửi tiếp vào hội thoại do SERVER soạn từ bảng ĐÃ CHUẨN HOÁ, không phải do trình duyệt ghép
    // từ payload nó đang giữ. Hai lý do, và lý do thứ hai mới là thứ mới: (1) hai bản lệch nhau thì hội
    // thoại kể một đằng còn file nguồn ghi một nẻo, mà mọi tầng chắt lọc tin vào bản kể; (2) câu mở đầu cố
    // định của nó chính là dấu hiệu để lượt chat kế tiếp biết mình là lượt BA KỂ LẠI cách hiểu file.
    [Fact]
    public async Task ExecuteAsync_ComposesTheChatMessage_FromWhatWasActuallySaved()
    {
        var result = await ConfirmAsync("""
            [{"fileName":"KeHoach.xlsx","column":"Global ID","meaning":"mã số nhân viên","used":true},
             {"fileName":"KeHoach.xlsx","column":"Item Title","meaning":"tên lớp học","used":true},
             {"fileName":"KeHoach.xlsx","column":"Revision Number","meaning":"phiên bản nội dung","used":false},
             {"fileName":"KeHoach.xlsx","column":"Sức chứa phòng","meaning":"bịa","used":true}]
            """);

        Assert.Equal(1, result.Files);
        Assert.True(SourceColumnMapBuilder.IsSubmissionMessage(result.Message));
        Assert.Contains("Global ID: mã số nhân viên", result.Message, StringComparison.Ordinal);
        // Cột bỏ tích được GỌI TÊN: im lặng bỏ chúng thì người dùng không có bằng chứng nào cho thấy mình
        // vừa loại đúng những cột định loại.
        Assert.Contains("Revision Number", result.Message, StringComparison.Ordinal);
        // …còn dòng bịa thì không được kể lại như một điều người dùng vừa chốt.
        Assert.DoesNotContain("Sức chứa phòng", result.Message, StringComparison.Ordinal);
    }

    private async Task<int> ExecuteAsync(string? mapJson) => (await ConfirmAsync(mapJson)).Files;

    private async Task<ConfirmColumnMapResult> ConfirmAsync(string? mapJson)
    {
        await using var db = NewDb();
        return await new ConfirmSourceColumnMapUseCase(db).ExecuteAsync(_projectId, mapJson);
    }

    private async Task<string?> LoadColumnMapAsync()
    {
        await using var db = NewDb();
        return (await db.ProjectSourceFiles.FirstAsync(s => s.Id == _sourceId)).ColumnMap;
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
