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

// Bảng phân quyền người dùng vừa chọn được lưu lên chính dự án — từ đó BAChatService (thôi hỏi lại),
// RequirementCoverageService (nhóm phân quyền [RÕ] có căn cứ thật) và prompt sinh AI Design Spec (POC dựng
// UI theo vai) đọc ra. Payload tới TỪ TRÌNH DUYỆT nên use case không được tin nó, kể cả khi chính server
// vừa render bảng đó ra vài giây trước.
public class ConfirmPermissionMatrixUseCaseTests : IDisposable
{
    // Phạm vi màn hình của dự án — các DÒNG mà bảng phân quyền đứng lên.
    private const string ScreenScope = """
        [{"screen":"Màn hình Training Plan để tạo và quản lý kế hoạch cho cả năm","included":true,"confirmedByUser":true},
         {"screen":"Màn hình Admin xem xét và xử lý các ticket ở trạng thái waitlist","included":true,"confirmedByUser":true}]
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public ConfirmPermissionMatrixUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "Kế hoạch đào tạo", ScreenScopeMap = ScreenScope });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_StoresWhatTheUserChose_AndComposesTheChatMessage()
    {
        var result = await ExecuteAsync("""
            [{"screen":"Màn hình Training Plan để tạo và quản lý kế hoạch cho cả năm","function":"Xem","condition":"",
              "grants":[{"role":"HR Assistant","scope":"của mình"},{"role":"HOD HR","scope":"tất cả"}]},
             {"screen":"Màn hình Training Plan để tạo và quản lý kế hoạch cho cả năm","function":"Xóa","condition":"",
              "grants":[{"role":"HR Assistant","scope":""},{"role":"HOD HR","scope":""}]}]
            """);

        Assert.True(result.Rows > 0);

        var stored = PermissionMatrixBuilder.Parse(await LoadMatrixAsync());
        // Lọc theo CẢ màn hình: màn hình waitlist mà client không gửi dòng nào vẫn được bù vào bảng với
        // một dòng "Xem" (xem PermissionMatrixBuilder), nên chỉ lọc theo tên chức năng là dính cả hai.
        var view = stored.Single(r => r.Function == "Xem" && r.Screen.Contains("Training Plan"));
        Assert.Equal("của mình", view.Grants.Single(g => g.Role == "HR Assistant").Scope);
        Assert.Equal("tất cả", view.Grants.Single(g => g.Role == "HOD HR").Scope);

        // Tin nhắn do SERVER soạn từ bảng ĐÃ CHUẨN HOÁ (không phải bảng client gửi): hai bản lệch nhau thì
        // hội thoại kể một đằng còn dữ liệu dự án ghi một nẻo, mà mọi tầng đọc transcript tin vào bản kể.
        Assert.Contains("Xem: HR Assistant (của mình), HOD HR (tất cả)", result.Message);
        Assert.Contains("Xóa: không vai nào được làm", result.Message);
    }

    // Màn hình client gửi lên mà không có trong phạm vi đã chắt: bỏ. Ngược lại, màn hình có trong phạm vi
    // mà client bỏ sót vẫn được bù vào — người dùng phải thấy nó để quyết, chứ không phải im lặng mất quyền.
    [Fact]
    public async Task ExecuteAsync_DropsUnknownScreens_AndKeepsEveryPlannedOne()
    {
        await ExecuteAsync("""
            [{"screen":"Màn hình Training Plan để tạo và quản lý kế hoạch cho cả năm","function":"Xem",
              "grants":[{"role":"HR Assistant","scope":"tất cả"}]},
             {"screen":"Màn hình báo cáo ngân sách","function":"Xem",
              "grants":[{"role":"HR Assistant","scope":"tất cả"}]}]
            """);

        var stored = PermissionMatrixBuilder.Parse(await LoadMatrixAsync());
        Assert.DoesNotContain(stored, r => r.Screen.Contains("ngân sách"));
        Assert.Contains(stored, r => r.Screen.Contains("waitlist"));
    }

    // Payload rỗng/hỏng: không ghi gì và báo về 0 dòng để UI GIỮ bảng lại. Ghi một bảng rỗng đè lên là mất
    // đường chốt phân quyền của dự án mà chẳng ai biết — và nhóm phân quyền thì không có nguồn nào khác.
    [Fact]
    public async Task ExecuteAsync_StoresNothing_WhenThePayloadIsEmptyOrBroken()
    {
        Assert.Equal(0, (await ExecuteAsync("[]")).Rows);
        Assert.Equal(0, (await ExecuteAsync("{ hỏng")).Rows);
        Assert.Equal(0, (await ExecuteAsync(null)).Rows);
        Assert.Null(await LoadMatrixAsync());
    }

    // Dự án chưa chắt được phạm vi màn hình nào ⇒ không có dòng nào hợp lệ để lưu. Lưu bừa theo tên client
    // gửi là mở đúng cửa mà PermissionMatrixBuilder sinh ra để đóng.
    [Fact]
    public async Task ExecuteAsync_StoresNothing_WhenTheProjectHasNoScreenScope()
    {
        await using (var db = NewDb())
        {
            (await db.Projects.FirstAsync(p => p.Id == _projectId)).ScreenScopeMap = null;
            await db.SaveChangesAsync();
        }

        var result = await ExecuteAsync("""
            [{"screen":"Màn hình Training Plan để tạo và quản lý kế hoạch cho cả năm","function":"Xem",
              "grants":[{"role":"HR Assistant","scope":"tất cả"}]}]
            """);

        Assert.Equal(0, result.Rows);
        Assert.Null(await LoadMatrixAsync());
    }

    // BẢNG VAI TRÒ đi cùng chuyến với bảng quyền. Vai người dùng THÊM mà chưa cấp quyền ở dòng nào vẫn phải
    // thành một cột trong bảng đã lưu: cột đó là câu trả lời "vai này không có quyền gì ở đây" — một quyết
    // định — chứ không phải một dòng trống để bỏ đi. Vai họ XÓA thì biến mất khỏi mọi dòng, kể cả khi
    // payload còn mang grants cũ của nó (bảng cũ trong một tab mở song song).
    [Fact]
    public async Task ExecuteAsync_TakesTheColumnsFromTheRoleTable_NotFromTheGrants()
    {
        var result = await ExecuteAsync("""
            [{"screen":"Màn hình Training Plan để tạo và quản lý kế hoạch cho cả năm","function":"Xem","condition":"",
              "grants":[{"role":"HR Assistant","scope":"của mình"},{"role":"HOD HR","scope":"tất cả"}]}]
            """, """["HR Assistant","Admin"]""");

        Assert.True(result.Rows > 0);

        var stored = PermissionMatrixBuilder.Parse(await LoadMatrixAsync());
        Assert.All(stored, r => Assert.Equal(new[] { "HR Assistant", "Admin" }, r.Grants.Select(g => g.Role)));
        // Cột vừa thêm có mặt ở MỌI dòng, ở trạng thái chưa có quyền.
        Assert.All(stored, r => Assert.False(PermissionScope.IsGranted(r.Grants.Single(g => g.Role == "Admin").Scope)));
        // Quyền của cột còn lại không bị bản đồng bộ cột làm rơi.
        Assert.Equal("của mình", stored
            .Single(r => r.Function == "Xem" && r.Screen.Contains("Training Plan"))
            .Grants.Single(g => g.Role == "HR Assistant").Scope);
    }

    // Bảng vai trò gửi lên mà TRỐNG TRƠN là người dùng vừa xóa hết vai: không lưu gì, và trả về câu nói rõ
    // phải sửa gì. Lưu một bảng không có cột nào là tệ nhất — cột PermissionMatrix có dữ liệu ⇒
    // PermissionMatrixGate coi như đã chốt và không bao giờ bày lại bảng, nên không còn đường sửa.
    [Fact]
    public async Task ExecuteAsync_StoresNothing_WhenTheRoleTableCameBackEmpty()
    {
        var result = await ExecuteAsync("""
            [{"screen":"Màn hình Training Plan để tạo và quản lý kế hoạch cho cả năm","function":"Xem",
              "grants":[{"role":"HR Assistant","scope":"tất cả"}]}]
            """, """["", "   "]""");

        Assert.Equal(0, result.Rows);
        Assert.Contains("ít nhất một vai trò", result.Error);
        Assert.Null(await LoadMatrixAsync());
    }

    // Tab mở từ TRƯỚC bản có bảng vai trò không gửi trường nào: rơi về đúng hành vi cũ (cột chắt từ grants),
    // không phải bị từ chối lưu.
    [Fact]
    public async Task ExecuteAsync_FallsBackToTheGrants_WhenNoRoleTableWasSent()
    {
        var result = await ExecuteAsync("""
            [{"screen":"Màn hình Training Plan để tạo và quản lý kế hoạch cho cả năm","function":"Xem",
              "grants":[{"role":"HR Assistant","scope":"tất cả"}]}]
            """);

        Assert.True(result.Rows > 0);
        Assert.Equal(new[] { "HR Assistant" }, PermissionMatrixBuilder.Roles(await LoadMatrixAsync()));
    }

    private async Task<ConfirmPermissionMatrixUseCase.Result> ExecuteAsync(string? matrixJson, string? rolesJson = null)
    {
        await using var db = NewDb();
        return await new ConfirmPermissionMatrixUseCase(db).ExecuteAsync(_projectId, matrixJson, rolesJson);
    }

    private async Task<string?> LoadMatrixAsync()
    {
        await using var db = NewDb();
        return (await db.Projects.FirstAsync(p => p.Id == _projectId)).PermissionMatrix;
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
