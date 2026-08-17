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
    // Bản GIEO của danh sách người nhận lấy vai trò từ bảng phân quyền đã chốt — ở đây chỉ cần đủ để
    // SeedRecipients dựng ra mục "HRBP" cho dự án chưa từng sửa danh sách.
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
              "to":["Người tạo"],"cc":["HRBP"]},
             {"entity":"Đơn đăng ký","event":"Chờ duyệt","needed":false}]
            """);

        Assert.Equal(2, result.Rows);
        Assert.Empty(result.Error);

        var stored = NotificationMapBuilder.Parse(await LoadMapAsync());
        var approved = stored.Single(r => r.Event == "Đã duyệt");
        Assert.Equal(new[] { "Người tạo" }, approved.To);
        Assert.Equal(new[] { "HRBP" }, approved.Cc);
        Assert.False(stored.Single(r => r.Event == "Chờ duyệt").Needed);

        Assert.Contains("To: Người tạo; CC: HRBP", result.Message);
        Assert.Contains("KHÔNG cần gửi thông báo", result.Message);
    }

    // ==== DANH SÁCH NGƯỜI NHẬN: đi CÙNG CHUYẾN với bảng ====

    // Người dùng tự thêm một người nhận vào bảng danh sách rồi chọn nó ở một sự kiện. Đây là ca mà bản
    // trước không có đường nào đi: danh sách là tất định, nên mọi mục ngoài nó bị bỏ ở đúng lượt lưu và lối
    // thoát duy nhất của người dùng là bỏ tích "Cần" — tức ghi vào tài liệu "sự kiện này không cần email"
    // chỉ vì thiếu một tùy chọn.
    [Fact]
    public async Task ExecuteAsync_AcceptsARecipientTheUserAddedToTheList_AndStoresTheList()
    {
        var result = await ExecuteAsync("""
            [{"entity":"Đơn đăng ký","event":"Đã duyệt","needed":true,"to":["Ban giám đốc"]}]
            """,
            """["Người tạo","HRBP","Ban giám đốc"]""");

        Assert.Equal(1, result.Rows);
        Assert.Equal(new[] { "Ban giám đốc" },
            NotificationMapBuilder.Parse(await LoadMapAsync()).Single().To);
        Assert.Equal(new[] { "Người tạo", "HRBP", "Ban giám đốc" },
            NotificationMapBuilder.ParseRecipients(await LoadRecipientsAsync()));
    }

    // Danh sách chỉ MỞ cho người dùng, không mở cho payload: bộ đối chiếu là chính danh sách vừa gửi lên,
    // nên một người nhận không có trong đó vẫn bị bỏ — và dòng rơi vào chốt chặn bất biến.
    [Fact]
    public async Task ExecuteAsync_StillDropsARecipientMissingFromTheSubmittedList()
    {
        var result = await ExecuteAsync("""
            [{"entity":"Đơn đăng ký","event":"Đã duyệt","needed":true,"to":["Phòng Nhân sự"]}]
            """,
            """["Người tạo","Ban giám đốc"]""");

        Assert.Equal(0, result.Rows);
        Assert.Contains("Đã duyệt", result.Error);
        Assert.Null(await LoadMapAsync());
    }

    // Danh sách rỗng (tab mở từ trước bản này, hoặc người dùng xóa sạch) KHÔNG có nghĩa "dự án không còn
    // người nhận nào": nó rơi về đúng bộ mà lượt bày bảng đã dùng, ở đây là bản gieo từ bảng phân quyền.
    [Fact]
    public async Task ExecuteAsync_FallsBackToTheSeededList_WhenTheSubmittedListIsEmpty()
    {
        var result = await ExecuteAsync("""
            [{"entity":"Đơn đăng ký","event":"Đã duyệt","needed":true,"to":["HRBP"]}]
            """,
            recipientsJson: null);

        Assert.Equal(1, result.Rows);
        Assert.Equal(new[] { "HRBP" }, NotificationMapBuilder.Parse(await LoadMapAsync()).Single().To);
        Assert.Equal(
            NotificationMapBuilder.SeedRecipients(new[] { "HRBP" }),
            NotificationMapBuilder.ParseRecipients(await LoadRecipientsAsync()));
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

    // Người nhận không khớp danh sách của dự án bị bỏ ⇒ dòng thành "tích mà rỗng" và
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

    private async Task<ConfirmNotificationMapUseCase.Result> ExecuteAsync(
        string? notificationsJson, string? recipientsJson = null)
    {
        await using var db = NewDb();
        return await new ConfirmNotificationMapUseCase(db)
            .ExecuteAsync(_projectId, notificationsJson, recipientsJson);
    }

    private async Task<string?> LoadMapAsync()
    {
        await using var db = NewDb();
        return (await db.Projects.FirstAsync(p => p.Id == _projectId)).NotificationMap;
    }

    private async Task<string?> LoadRecipientsAsync()
    {
        await using var db = NewDb();
        return (await db.Projects.FirstAsync(p => p.Id == _projectId)).NotificationRecipients;
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
