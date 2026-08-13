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

// BẢNG NGƯỜI DÙNG GỬI ĐI PHẢI LÀ BẢNG HỌ VỪA NHÌN THẤY.
//
// Bảng màn hình được dựng từ Project.PlannedScope, nhưng lượt chắt lọc "triển vọng phỏng vấn" GHI ĐÈ cột đó
// ở hậu kỳ ngay chính lượt bày bảng. Nên tới lúc người dùng bấm gửi, danh sách đối chiếu đã khác danh sách
// đã render — và chốt chặn "màn hình bịa" quay ra bắn vào chính bảng của server: mọi dòng trượt khỏi
// MatchScreen, phần người dùng điền bị bỏ, chỗ của nó là các mục phạm vi mới bù vào ở dạng TRẮNG.
//
// Hỏng kiểu này KHÔNG báo lỗi ở đâu cả: nút gửi vẫn chạy, hội thoại vẫn có tin nhắn "mình đã rà bảng màn
// hình", chỉ có điều nội dung rà đã biến mất và khối ngữ cảnh của bảng đã chốt thì cấm BA hỏi lại.
public class ConfirmScreenScopeUseCaseTests : IDisposable
{
    // Ba màn hình BA đã bày ra và người dùng đã rà.
    private const string ScreenList = "Màn hình quản lý danh sách JD trong nhà máy";
    private const string ScreenCreate = "Tính năng tạo và cập nhật JD";
    private const string ScreenAssign = "Tính năng gán JD cho từng nhân viên";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public ConfirmScreenScopeUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        var modelId = Guid.NewGuid();
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(new AiModel { Id = modelId, ModelId = "test" });
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = modelId });
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Quản lý JD",
            PlannedScope = Bullets(ScreenList, ScreenCreate, ScreenAssign)
        });
        db.SaveChanges();
    }

    // LỖI GỐC. Bảng render ra ba dòng; ngay sau đó lượt chắt lọc viết lại phạm vi thành tám mục diễn đạt
    // khác. Người dùng bấm gửi và phải nhận lại ĐÚNG ba dòng mình vừa điền — không phải tám dòng trắng.
    [Fact]
    public async Task ExecuteAsync_KeepsTheReviewedTable_WhenTheDistillRewrotePlannedScopeUnderneath()
    {
        await SeedRenderedTableAsync(ScreenList, ScreenCreate, ScreenAssign);
        await RewritePlannedScopeAsync(
            "Màn hình quản lý danh sách JD theo orgUnit",
            "Màn hình để manager tự tạo và cập nhật JD cho orgUnit của mình",
            "Tính năng manager submit JD để HRBP xác minh",
            "Tính năng manager gán JD available cho nhân viên thuộc quyền");

        var result = await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","purpose":"Quản lý và tra cứu danh sách",
              "functions":"Xem danh sách JD","flowSteps":["JD được chuyển sang trạng thái có thể gán"],"included":true},
             {"screen":"{{ScreenCreate}}","purpose":"Cho manager tạo JD cho orgUnit của mình",
              "functions":"Tạo JD, Cập nhật JD, Gửi duyệt","flowSteps":["Manager submit JD"],"included":true},
             {"screen":"{{ScreenAssign}}","purpose":"Cho manager gán JD đã được duyệt",
              "functions":"Chọn JD, Gán JD cho nhân viên","flowSteps":["Manager gán JD cho nhân viên"],"included":true}]
            """);

        Assert.Equal(3, result.Rows);

        var stored = ScreenScopeMapBuilder.Parse(await LoadScreenScopeAsync());
        Assert.Equal(3, stored.Count);
        // Phần đắt nhất của bảng là ba cột người dùng tự điền — mất chúng thì bảng chỉ còn là danh sách tên.
        Assert.Equal("Quản lý và tra cứu danh sách", stored.Single(r => r.Screen == ScreenList).Purpose);
        Assert.Equal("Tạo JD, Cập nhật JD, Gửi duyệt", stored.Single(r => r.Screen == ScreenCreate).Functions);
        Assert.Equal("Manager gán JD cho nhân viên", Assert.Single(stored.Single(r => r.Screen == ScreenAssign).FlowSteps));

        // Và tin nhắn kể lại phải chở đúng chừng đó, vì mọi tầng chắt lọc đọc bản kể chứ không đọc cột DB.
        Assert.Contains($"- {ScreenCreate} — Cho manager tạo JD cho orgUnit của mình [chức năng: Tạo JD, Cập nhật JD, Gửi duyệt]", result.Message);
        Assert.DoesNotContain("theo orgUnit", result.Message);
    }

    // Người dùng vừa tự tay duyệt phạm vi ⇒ bản duyệt thay cho bản LLM đoán. Không ghi ngược thì
    // EffectiveScreens bù lại đúng những mục chỉ khác CHỮ so với dòng vừa giữ, và bảng phân quyền ngay sau
    // đó mọc thêm một loạt dòng trùng nghĩa mà không dòng nào có việc của màn hình.
    [Fact]
    public async Task ExecuteAsync_WritesTheRatifiedScopeBackOntoTheProject()
    {
        await SeedRenderedTableAsync(ScreenList, ScreenCreate, ScreenAssign);
        await RewritePlannedScopeAsync("Màn hình quản lý danh sách JD theo orgUnit");

        await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","included":true},
             {"screen":"{{ScreenCreate}}","included":true},
             {"screen":"{{ScreenAssign}}","included":false}]
            """);

        var scope = InterviewOutlookService.ParseItems(await LoadPlannedScopeAsync());
        Assert.Equal(new[] { ScreenList, ScreenCreate }, scope);

        // Và phạm vi hiệu dụng — nguồn DÒNG của bảng phân quyền — không còn mục nào người dùng chưa duyệt.
        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        Assert.Equal(new[] { ScreenList, ScreenCreate }, PermissionMatrixGate.EffectiveScreens(project));
    }

    // Chốt chặn "màn hình bịa" vẫn còn nguyên, chỉ đổi thứ để đối chiếu: bảng server đã render, chứ không
    // phải một danh sách đã đổi dưới chân người dùng.
    [Fact]
    public async Task ExecuteAsync_DropsScreensThatWereNeverRendered()
    {
        await SeedRenderedTableAsync(ScreenList, ScreenCreate);

        await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","included":true},
             {"screen":"{{ScreenCreate}}","included":true},
             {"screen":"Màn hình quản trị hệ thống và phân quyền toàn nhà máy","included":true}]
            """);

        var stored = ScreenScopeMapBuilder.Parse(await LoadScreenScopeAsync());
        Assert.Equal(2, stored.Count);
        Assert.DoesNotContain(stored, r => r.Screen.Contains("quản trị hệ thống"));
    }

    // Bỏ tích SẠCH bảng: vẫn lưu bảng (đó là quyết định của họ) nhưng KHÔNG ghi ngược một phạm vi rỗng.
    // Ghi null vào PlannedScope là cắt luôn đường fail-open của EffectiveScreens và khóa chết cổng phân
    // quyền trong im lặng — nút "Write Requirement" không bao giờ sáng, không gì trên màn hình nói vì sao.
    [Fact]
    public async Task ExecuteAsync_LeavesPlannedScopeAlone_WhenEveryRowWasUnticked()
    {
        await SeedRenderedTableAsync(ScreenList, ScreenCreate);
        var before = await LoadPlannedScopeAsync();

        var result = await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","included":false},
             {"screen":"{{ScreenCreate}}","included":false}]
            """);

        Assert.Equal(2, result.Rows);
        Assert.Equal(before, await LoadPlannedScopeAsync());
    }

    // Không có lượt bảng nào để đối chiếu (lượt đã bị "New Chat" lưu trữ, hoặc dự án chốt bằng đường khác)
    // ⇒ quay về PlannedScope như trước. Fail-open: mất chốt chặn tên màn hình rẻ hơn nhiều so với một nút
    // gửi không bao giờ lưu được gì.
    [Fact]
    public async Task ExecuteAsync_FallsBackToPlannedScope_WhenNoRenderedTableSurvives()
    {
        await SeedRenderedTableAsync(archived: true, screens: new[] { ScreenList, ScreenCreate });

        var result = await ExecuteAsync($$"""
            [{"screen":"{{ScreenAssign}}","purpose":"Cho manager gán JD","included":true}]
            """);

        Assert.Equal(3, result.Rows); // ba mục PlannedScope, hai mục còn lại được bù vào
        var stored = ScreenScopeMapBuilder.Parse(await LoadScreenScopeAsync());
        Assert.Equal("Cho manager gán JD", stored.Single(r => r.Screen == ScreenAssign).Purpose);
    }

    // Payload rỗng/hỏng: không ghi gì và báo 0 dòng để UI GIỮ bảng lại — cùng luật với bảng phân quyền.
    [Fact]
    public async Task ExecuteAsync_StoresNothing_WhenThePayloadIsEmptyOrBroken()
    {
        await SeedRenderedTableAsync(ScreenList, ScreenCreate);

        Assert.Equal(0, (await ExecuteAsync("[]")).Rows);
        Assert.Equal(0, (await ExecuteAsync("{ hỏng")).Rows);
        Assert.Equal(0, (await ExecuteAsync(null)).Rows);
        Assert.Null(await LoadScreenScopeAsync());
    }

    // ==== dàn dựng ====

    private Task SeedRenderedTableAsync(params string[] screens) => SeedRenderedTableAsync(false, screens);

    /// <summary>Lượt BA BÀY BẢNG: bảng server render được lưu ở AgentConversation.ScreenScopeMap của lượt đó.</summary>
    private async Task SeedRenderedTableAsync(bool archived, string[] screens)
    {
        var rows = ScreenScopeMapBuilder.Build(
            screens.Select(s => new ICOGenerator.Contracts.Requirements.ScreenScopeRow { Screen = s }), screens);

        await using var db = NewDb();
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "assistant",
            Message = "Anh/chị vui lòng rà soát phạm vi màn hình dưới đây rồi bấm “Gửi bảng màn hình”.",
            ScreenScopeMap = System.Text.Json.JsonSerializer.Serialize(rows),
            ArchivedAt = archived ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Lượt chắt lọc hậu kỳ ghi đè phạm vi — chạy NGAY sau lượt bày bảng, trước khi người dùng bấm gửi.</summary>
    private async Task RewritePlannedScopeAsync(params string[] screens)
    {
        await using var db = NewDb();
        (await db.Projects.FirstAsync(p => p.Id == _projectId)).PlannedScope = Bullets(screens);
        await db.SaveChangesAsync();
    }

    private async Task<ConfirmScreenScopeUseCase.Result> ExecuteAsync(string? screensJson)
    {
        await using var db = NewDb();
        return await new ConfirmScreenScopeUseCase(db).ExecuteAsync(_projectId, screensJson);
    }

    private async Task<string?> LoadScreenScopeAsync()
    {
        await using var db = NewDb();
        return (await db.Projects.FirstAsync(p => p.Id == _projectId)).ScreenScopeMap;
    }

    private async Task<string?> LoadPlannedScopeAsync()
    {
        await using var db = NewDb();
        return (await db.Projects.FirstAsync(p => p.Id == _projectId)).PlannedScope;
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
