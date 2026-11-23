using ICOGenerator.Application.Requirements;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
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
    // Bản thiết kế UX của dự án — các TÁC VỤ là các DÒNG mà bảng phân quyền đứng lên.
    private static readonly string Blueprint = UxBlueprintBuilder.Serialize(new UxBlueprint
    {
        Tasks =
        {
            new UxTask { Id = "T-01", Name = "Xem kế hoạch", Placed = true },
            new UxTask { Id = "T-02", Name = "Xóa kế hoạch", Placed = true },
            new UxTask { Id = "T-03", Name = "Xử lý waitlist", Placed = true }
        },
        Screens =
        {
            new UxScreen { Id = "S-01", Name = "Training Plans", Actions = { new UxAction { Task = "T-01" }, new UxAction { Task = "T-02" } } },
            new UxScreen { Id = "S-02", Name = "Waitlist Queue", Actions = { new UxAction { Task = "T-03" } } }
        }
    });

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
        db.Projects.Add(new Project { Id = _projectId, Name = "Kế hoạch đào tạo", UxBlueprint = Blueprint });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_StoresWhatTheUserChose()
    {
        var result = await ExecuteAsync("""
            [{"taskId":"T-01","screen":"Training Plans","function":"Xem kế hoạch","condition":"",
              "grants":[{"role":"HR Assistant","scope":"của mình"},{"role":"HOD HR","scope":"tất cả"}]},
             {"taskId":"T-02","screen":"Training Plans","function":"Xóa kế hoạch","condition":"",
              "grants":[{"role":"HR Assistant","scope":""},{"role":"HOD HR","scope":""}]}]
            """);

        Assert.True(result.Rows > 0);

        var stored = PermissionMatrixBuilder.Parse(await LoadMatrixAsync());
        var view = stored.Single(r => r.TaskId == "T-01");
        Assert.Equal(PermissionScope.Own, view.Grants.Single(g => g.Role == "HR Assistant").Scope);
        Assert.Equal(PermissionScope.All, view.Grants.Single(g => g.Role == "HOD HR").Scope);

        // Lưu ở popup CHỈ là lưu: không có tin nhắn nào gửi tiếp vào khung chat, nên không có hội thoại nào
        // ghi thêm lượt.
        await using var db = NewDb();
        Assert.Empty(await db.AgentConversations.Where(c => c.ProjectId == _projectId).ToListAsync());
    }

    // Dòng client gửi lên mà trỏ vào tác vụ không có thật: bỏ. Ngược lại, tác vụ client bỏ sót vẫn được bù
    // vào — người dùng phải thấy nó để quyết, chứ không phải im lặng mất quyền.
    [Fact]
    public async Task ExecuteAsync_DropsUnknownTasks_AndKeepsEveryDesignedOne()
    {
        await ExecuteAsync("""
            [{"taskId":"T-01","screen":"Training Plans","function":"Xem kế hoạch",
              "grants":[{"role":"HR Assistant","scope":"tất cả"}]},
             {"taskId":"T-99","screen":"Màn hình báo cáo ngân sách","function":"Xem",
              "grants":[{"role":"HR Assistant","scope":"tất cả"}]}]
            """);

        var stored = PermissionMatrixBuilder.Parse(await LoadMatrixAsync());
        Assert.DoesNotContain(stored, r => r.TaskId == "T-99");
        Assert.Contains(stored, r => r.TaskId == "T-03" && r.Screen == "Waitlist Queue");
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

    // Dự án chưa có bản thiết kế UX ⇒ không có dòng nào hợp lệ để lưu. Lưu bừa theo tên client
    // gửi là mở đúng cửa mà PermissionMatrixBuilder sinh ra để đóng.
    [Fact]
    public async Task ExecuteAsync_StoresNothing_WhenTheProjectHasNoBlueprint()
    {
        await using (var db = NewDb())
        {
            (await db.Projects.FirstAsync(p => p.Id == _projectId)).UxBlueprint = null;
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
            [{"taskId":"T-01","screen":"Training Plans","function":"Xem kế hoạch","condition":"",
              "grants":[{"role":"HR Assistant","scope":"của mình"},{"role":"HOD HR","scope":"tất cả"}]}]
            """, """["HR Assistant","Admin"]""");

        Assert.True(result.Rows > 0);

        var stored = PermissionMatrixBuilder.Parse(await LoadMatrixAsync());
        Assert.All(stored, r => Assert.Equal(new[] { "HR Assistant", "Admin" }, r.Grants.Select(g => g.Role)));
        // Cột vừa thêm có mặt ở MỌI dòng, ở trạng thái chưa có quyền.
        Assert.All(stored, r => Assert.False(PermissionScope.IsGranted(r.Grants.Single(g => g.Role == "Admin").Scope)));
        // Quyền của cột còn lại không bị bản đồng bộ cột làm rơi.
        Assert.Equal(PermissionScope.Own, stored
            .Single(r => r.TaskId == "T-01")
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
}
