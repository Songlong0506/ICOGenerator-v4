using System.Text.Json;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// MỘT QUYẾT ĐỊNH CHỐT Ở BẢNG NÀY ĐẺ RA MỘT MÀN HÌNH Ở BẢNG KHÁC.
//
// Thông tin có nguồn "ứng dụng tự quản lý" nghĩa là ứng dụng phải có một màn hình CRUD riêng cho danh mục
// đó. Nhưng thứ tự phụ thuộc của buổi phỏng vấn là luồng → màn hình → đối tượng → phân quyền → thông báo,
// nên tới lúc bảng đối tượng bày ra thì bảng MÀN HÌNH đã chốt xong từ mấy lượt trước.
//
// Không gieo ngược lên Project.PlannedScope thì màn hình ấy không có mục nào ở "## 6. Screens To Generate"
// và không có DÒNG nào trong bảng phân quyền — tức mặc nhiên "không ai được xem" một màn hình mà người dùng
// vừa đặt hàng. Và hỏng kiểu này không báo lỗi ở đâu: bảng vẫn lưu, tin nhắn vẫn gửi, chỉ có màn hình là
// không bao giờ tồn tại.
public class ConfirmEntityMapUseCaseTests : IDisposable
{
    private const string ScreenList = "Màn hình quản lý danh sách JD trong nhà máy";
    private const string ScreenCreate = "Tính năng tạo và cập nhật JD";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public ConfirmEntityMapUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Quản lý JD",
            PlannedScope = Bullets(ScreenList, ScreenCreate)
        });
        db.SaveChanges();
    }

    // Ca gốc: người dùng chọn "ứng dụng tự quản lý" cho OrgUnit ⇒ phạm vi phải mọc thêm đúng một mục, và
    // ScreenScopeGate sẽ mở lại bảng màn hình từ chính mục đó.
    [Fact]
    public async Task ExecuteAsync_AddsAScreenForEveryAppManagedList()
    {
        var result = await ExecuteAsync(Table(
            Field("OrgUnit", EntityFieldInput.ChoiceOne, EntityFieldSource.App),
            Field("PC Level")));

        Assert.Equal(1, result.Rows);
        Assert.Equal(
            new[] { ScreenList, ScreenCreate, "Màn hình quản lý danh mục OrgUnit" },
            InterviewOutlookService.ParseItems(await LoadPlannedScopeAsync()));
    }

    // GHÉP THÊM chứ không ghi đè. PlannedScope là danh sách người dùng đã tự tay rà ở bảng màn hình
    // (ConfirmScreenScopeUseCase ghi ngược lên đây) — thay nó bằng mấy dòng danh mục là xoá sạch phạm vi đã
    // duyệt, và bảng phân quyền ngay sau đó mất gần hết dòng.
    [Fact]
    public async Task ExecuteAsync_KeepsTheReviewedScopeUntouched_WhenNoListIsAppManaged()
    {
        await ExecuteAsync(Table(
            Field("JobTitle", EntityFieldInput.ChoiceMany, EntityFieldSource.External, system: "HR_Portal"),
            Field("Mã JD", EntityFieldInput.Auto)));

        Assert.Equal(
            new[] { ScreenList, ScreenCreate },
            InterviewOutlookService.ParseItems(await LoadPlannedScopeAsync()));
    }

    // Gửi lại cùng một bảng (người dùng sửa một ô rồi bấm gửi lần nữa, hoặc BA bày lại bảng) KHÔNG được đẻ
    // thêm dòng: mục trùng đi vào phạm vi là hai dòng trùng nghĩa trong bảng màn hình mà không dòng nào có
    // việc của màn hình.
    [Fact]
    public async Task ExecuteAsync_DoesNotSeedTheSameScreenTwice()
    {
        var table = Table(Field("OrgUnit", EntityFieldInput.ChoiceOne, EntityFieldSource.App));
        await ExecuteAsync(table);
        await ExecuteAsync(table);

        Assert.Equal(
            new[] { ScreenList, ScreenCreate, "Màn hình quản lý danh mục OrgUnit" },
            InterviewOutlookService.ParseItems(await LoadPlannedScopeAsync()));
    }

    // Payload rỗng/hỏng ⇒ không ghi gì, kể cả phạm vi. Nửa vời ở đây là trạng thái tệ nhất: phạm vi mọc thêm
    // một màn hình cho một bảng đối tượng chưa bao giờ được lưu.
    [Fact]
    public async Task ExecuteAsync_WritesNothing_WhenThePayloadIsUnusable()
    {
        var result = await ExecuteAsync("[]");

        Assert.Equal(0, result.Rows);
        Assert.Equal(
            new[] { ScreenList, ScreenCreate },
            InterviewOutlookService.ParseItems(await LoadPlannedScopeAsync()));
        Assert.Null(await LoadEntityMapAsync());
    }

    // ==== dựng payload ====

    private static EntityFieldNote Field(
        string name, string input = EntityFieldInput.Text, string source = "", string system = "")
        => new()
        {
            Name = name,
            Used = true,
            Input = input,
            Source = source,
            SourceSystem = system
        };

    private static string Table(params EntityFieldNote[] fields)
        => JsonSerializer.Serialize(new[]
        {
            new EntityMapRow
            {
                Entity = "Bản mô tả công việc",
                Included = true,
                Fields = fields.ToList()
            }
        });

    private async Task<ConfirmEntityMapUseCase.Result> ExecuteAsync(string? entitiesJson)
    {
        await using var db = NewDb();
        return await new ConfirmEntityMapUseCase(db).ExecuteAsync(_projectId, entitiesJson);
    }

    private async Task<string?> LoadPlannedScopeAsync()
    {
        await using var db = NewDb();
        return (await db.Projects.FirstAsync(p => p.Id == _projectId)).PlannedScope;
    }

    private async Task<string?> LoadEntityMapAsync()
    {
        await using var db = NewDb();
        return (await db.Projects.FirstAsync(p => p.Id == _projectId)).EntityMap;
    }

    private static string Bullets(params string[] items) => string.Join("\n", items.Select(i => "- " + i));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
