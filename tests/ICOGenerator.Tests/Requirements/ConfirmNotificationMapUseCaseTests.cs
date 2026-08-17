using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Đường GỬI của bảng thông báo — chỗ BẤT BIẾN của bảng được chặn thật (popup trên trình duyệt chỉ là phanh
// phụ: nó không thấy payload sửa tay, tab mở từ trước bản này, hay lần bấm gửi lại sau khi mất mạng).
//
// Bất biến: một dòng đã lưu chỉ có HAI trạng thái — bỏ tích ("không gửi email") hoặc còn tích KÈM người
// nhận chính. Trạng thái thứ ba từng được cho qua và trả giá đúng bằng thứ cả cái bảng sinh ra để thay thế:
// nhóm «Thông báo / nhắc nhở» xuống [MỘT PHẦN], nút "Write Requirement" khóa, và BA phải đi hỏi lại TỪNG sự
// kiện trong khung chat, mỗi sự kiện hai lượt (To rồi CC). Ca thật ở dự án JD Library: bảng 8 dòng gửi đi
// với 7 dòng trống ⇒ 14 lượt chat, ở cuối một buổi phỏng vấn đã 78 lượt.
//
// Lưu MỘT PHẦN còn tệ hơn không lưu: cột NotificationMap có dữ liệu ⇒ NotificationMapGate coi như đã chốt
// và không bao giờ bày lại bảng, nên các dòng còn dở không còn màn hình nào để sửa.
public class ConfirmNotificationMapUseCaseTests : IDisposable
{
    // Vai trò của danh sách người nhận lấy từ bảng phân quyền đã chốt (không phải payload) — ở đây chỉ cần
    // đủ để RecipientOptions dựng ra mục "Toàn bộ HRBP".
    private const string ConfirmedMatrix = """
        [{"screen":"Màn hình đơn đăng ký","function":"Xem","condition":"",
          "grants":[{"role":"HRBP","scope":"tất cả"}]}]
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public ConfirmNotificationMapUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Đơn đăng ký",
            PermissionMatrix = ConfirmedMatrix
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_StoresWhatTheUserChose_AndComposesTheChatMessage()
    {
        var result = await ExecuteAsync("""
            [{"entity":"Đơn đăng ký","event":"Đã duyệt","trigger":"quản lý bấm duyệt","needed":true,
              "to":["Người tạo"],"cc":["Toàn bộ HRBP"]},
             {"entity":"Đơn đăng ký","event":"Chờ duyệt","needed":false}]
            """);

        Assert.Equal(2, result.Rows);
        Assert.Empty(result.Error);

        var stored = NotificationMapBuilder.Parse(await LoadMapAsync());
        var approved = stored.Single(r => r.Event == "Đã duyệt");
        Assert.Equal(new[] { "Người tạo" }, approved.To);
        Assert.Equal(new[] { "Toàn bộ HRBP" }, approved.Cc);
        Assert.False(stored.Single(r => r.Event == "Chờ duyệt").Needed);

        Assert.Contains("To: Người tạo; CC: Toàn bộ HRBP", result.Message);
        Assert.Contains("KHÔNG cần gửi thông báo", result.Message);
    }

    // Chốt chặn của bất biến: KHÔNG lưu gì, và câu trả về GỌI TÊN đúng các sự kiện còn thiếu — một câu
    // "bảng chưa hợp lệ" thì bắt người dùng tự rà lại tới 24 dòng.
    [Fact]
    public async Task ExecuteAsync_StoresNothing_WhenATickedEventHasNoRecipient()
    {
        var result = await ExecuteAsync("""
            [{"entity":"Đơn đăng ký","event":"Đã duyệt","needed":true,"to":["Người tạo"]},
             {"entity":"Đơn đăng ký","event":"Bị từ chối","trigger":"quản lý từ chối","needed":true,"to":[]}]
            """);

        Assert.Equal(0, result.Rows);
        Assert.Empty(result.Message);
        Assert.Contains("Bị từ chối", result.Error);
        Assert.Contains("chưa chọn người nhận", result.Error);
        Assert.Null(await LoadMapAsync());
    }

    // Người nhận không khớp danh sách chọn dựng LẠI từ bảng phân quyền bị bỏ ⇒ dòng thành "tích mà rỗng" và
    // rơi vào đúng chốt chặn trên. Đây là ca thật của một payload sửa tay: nếu lưu, spec và POC nhận một
    // người nhận không tầng nào kiểm được.
    [Fact]
    public async Task ExecuteAsync_StoresNothing_WhenTheOnlyRecipientIsOutsideTheOptionList()
    {
        var result = await ExecuteAsync("""
            [{"entity":"Đơn đăng ký","event":"Đã duyệt","needed":true,"to":["Phòng Nhân sự"]}]
            """);

        Assert.Equal(0, result.Rows);
        Assert.Contains("Đã duyệt", result.Error);
        Assert.Null(await LoadMapAsync());
    }

    // Bỏ tích SẠCH bảng là một quyết định hợp lệ ("ứng dụng này không gửi email nào") — không phải chỗ còn
    // thiếu, nên nó lưu được và tin nhắn phải nói đúng điều đó.
    [Fact]
    public async Task ExecuteAsync_StoresATableWhereEveryEventIsTurnedOff()
    {
        var result = await ExecuteAsync("""
            [{"entity":"Đơn đăng ký","event":"Đã duyệt","needed":false},
             {"entity":"Đơn đăng ký","event":"Chờ duyệt","needed":false}]
            """);

        Assert.Equal(2, result.Rows);
        Assert.Contains("không sự kiện nào cần gửi email", result.Message);
        Assert.NotNull(await LoadMapAsync());
    }

    // Payload rỗng/hỏng: không ghi gì, và câu lỗi để trống để trình duyệt in câu "bấm gửi lại" của nó —
    // đây là ca mạng chập, không phải bảng sai.
    [Fact]
    public async Task ExecuteAsync_StoresNothing_WhenThePayloadIsEmptyOrBroken()
    {
        foreach (var payload in new[] { "[]", "{ hỏng", null })
        {
            var result = await ExecuteAsync(payload);
            Assert.Equal(0, result.Rows);
            Assert.Empty(result.Error);
        }

        Assert.Null(await LoadMapAsync());
    }

    private async Task<ConfirmNotificationMapUseCase.Result> ExecuteAsync(string? notificationsJson)
    {
        await using var db = NewDb();
        return await new ConfirmNotificationMapUseCase(db).ExecuteAsync(_projectId, notificationsJson);
    }

    private async Task<string?> LoadMapAsync()
    {
        await using var db = NewDb();
        return (await db.Projects.FirstAsync(p => p.Id == _projectId)).NotificationMap;
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
