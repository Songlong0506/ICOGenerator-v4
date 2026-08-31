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
// đó. Chính hàm gieo này là lý do thứ tự phụ thuộc của buổi phỏng vấn đặt bảng đối tượng TRƯỚC bảng màn
// hình: luồng → đối tượng → báo cáo → màn hình → phân quyền → thông báo. Gieo trước lần bày đầu thì các màn
// hình danh mục là những dòng bình thường của bảng màn hình, người dùng tích/bỏ tích ngay tại đó.
//
// Không gieo vào bảng màn hình thì màn hình ấy không có mục nào ở "## 6. Screens To Generate"
// và không có DÒNG nào trong bảng phân quyền — tức mặc nhiên "không ai được xem" một màn hình mà người dùng
// vừa đặt hàng. Và hỏng kiểu này không báo lỗi ở đâu: bảng vẫn lưu, tin nhắn vẫn gửi, chỉ có màn hình là
// không bao giờ tồn tại.
public class ConfirmEntityMapUseCaseTests : IDisposable
{
    private const string ScreenList = "Màn hình quản lý danh sách JD trong nhà máy";
    private const string ScreenCreate = "Tính năng tạo và cập nhật JD";

    private const string Coverage = """
        - ★ Mục tiêu / bài toán: [RÕ] Quản lý JD. {nguồn: "quản lý JD trong nhà máy"}
        - ★ Đối tượng người dùng & vai trò: [RÕ] Manager lập, HRBP verify. {nguồn: "Manager lập, HRBP verify"}
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Tạo JD, submit, duyệt. {nguồn: "Đúng luồng này"}
        - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] HOD từ chối thì Manager sửa lại. {nguồn: "trả về sửa lại"}
        - Dữ liệu / danh mục chính: [RÕ] OrgUnit, JobTitle, PC Level. {nguồn: "các danh mục này"}
        - Vòng đời & trạng thái: [RÕ] Draft → Chờ duyệt → Available. {nguồn: "duyệt xong là dùng được"}
        - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] Chưa cần. {nguồn: "hiện tại chưa cần"}
        - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
        """;

    private const string ConfirmedFlow = """
        [{"name":"Tạo JD","kind":"luồng chính","role":"Manager","steps":[
          {"actor":"Manager","action":"Tạo JD","outcome":"Draft","included":true},
          {"actor":"HRBP","action":"Verify JD","outcome":"Chờ HOD duyệt","included":true}]}]
        """;

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
            // Phạm vi đã chắt từ hội thoại, chưa ai rà.
            ScreenScopeMap = PendingTable(ScreenList, ScreenCreate),
            // Trạng thái của một dự án vừa chốt bảng luồng: đủ để cổng bảng màn hình mở ngay sau lượt chốt
            // bảng đối tượng — xem ExecuteAsync_PutsTheSeededScreenIntoTheFirstScreenScopeTable.
            RequirementCoverageMap = Coverage,
            FlowMap = ConfirmedFlow
        });
        db.SaveChanges();
    }

    // Ca gốc: người dùng chọn "ứng dụng tự quản lý" cho OrgUnit ⇒ phạm vi phải mọc thêm đúng một mục, và
    // bảng màn hình (bày SAU bảng này) sẽ có nó ngay ở lần bày đầu.
    [Fact]
    public async Task ExecuteAsync_AddsAScreenForEveryAppManagedList()
    {
        var result = await ExecuteAsync(Table(
            Field("OrgUnit", EntityFieldInput.ChoiceOne, EntityFieldSource.App),
            Field("PC Level")));

        Assert.Equal(1, result.Rows);
        Assert.Equal(
            new[] { ScreenList, ScreenCreate, "OrgUnit Catalog" },
            ScreenScopeMapBuilder.EffectiveScreens(await LoadScreenScopeAsync()));
    }

    // CHỈ THÊM, không đụng tới dòng nào đang có. Bảng màn hình có thể đã được người dùng tự tay rà — ghi
    // đè nó bằng mấy dòng danh mục là xoá sạch phạm vi đã duyệt, và bảng phân quyền ngay sau đó mất gần
    // hết dòng.
    [Fact]
    public async Task ExecuteAsync_KeepsTheReviewedScopeUntouched_WhenNoListIsAppManaged()
    {
        await ExecuteAsync(Table(
            Field("JobTitle", EntityFieldInput.ChoiceMany, EntityFieldSource.External, system: "HR_Portal"),
            Field("Mã JD", EntityFieldInput.Auto)));

        Assert.Equal(
            new[] { ScreenList, ScreenCreate },
            ScreenScopeMapBuilder.EffectiveScreens(await LoadScreenScopeAsync()));
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
            new[] { ScreenList, ScreenCreate, "OrgUnit Catalog" },
            ScreenScopeMapBuilder.EffectiveScreens(await LoadScreenScopeAsync()));
    }

    // CẢ CHUỖI, không chỉ một cột: sau khi bảng đối tượng chốt, bảng kế tiếp phải là bảng MÀN HÌNH và màn
    // hình danh mục vừa gieo phải nằm trong danh sách DÒNG mà lượt bày ra dùng — tức nó đi vào LẦN BÀY ĐẦU
    // chứ không phải qua đường mở lại. Đây là bất biến mà thứ tự cũ (màn hình trước đối tượng) không giữ
    // được: ở đó người dùng đã chốt "đây là toàn bộ màn hình" từ mấy lượt trước rồi mới thấy danh sách dài
    // thêm, và cổng KHÔNG MÂU THUẪN bắn một mâu thuẫn cho đúng cái phạm vi vừa đổi.
    [Fact]
    public async Task ExecuteAsync_PutsTheSeededScreenIntoTheFirstScreenScopeTable()
    {
        await ExecuteAsync(Table(Field("OrgUnit", EntityFieldInput.ChoiceOne, EntityFieldSource.App)));

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);

        Assert.Equal(InterviewTableKind.ScreenScope, InterviewTableGate.Select(project));
        Assert.Contains("OrgUnit Catalog", PermissionMatrixGate.EffectiveScreens(project));
        // Bảng màn hình chưa chốt ⇒ đây là lần bày ĐẦU, không phải lượt "BỔ SUNG BẢNG MÀN HÌNH ĐÃ CHỐT".
        Assert.False(ScreenScopeMapBuilder.IsConfirmed(project.ScreenScopeMap));
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
            ScreenScopeMapBuilder.EffectiveScreens(await LoadScreenScopeAsync()));
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

    private async Task<string?> LoadScreenScopeAsync()
    {
        await using var db = NewDb();
        return (await db.Projects.FirstAsync(p => p.Id == _projectId)).ScreenScopeMap;
    }

    /// <summary>Bảng màn hình đã chắt từ hội thoại nhưng CHƯA AI RÀ.</summary>
    private static string PendingTable(params string[] screens)
        => JsonSerializer.Serialize(
            screens.Select(s => new ScreenScopeRow { Screen = s, Included = true }).ToList());

    private async Task<string?> LoadEntityMapAsync()
    {
        await using var db = NewDb();
        return (await db.Projects.FirstAsync(p => p.Id == _projectId)).EntityMap;
    }


    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
