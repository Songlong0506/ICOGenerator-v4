using ICOGenerator.Application.Requirements;
using ICOGenerator.Contracts.Requirements;
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
// Giữa lúc BA bày bảng và lúc người dùng bấm gửi vẫn có thể có một lượt chat khác, và lượt chắt lọc chạy ở
// hậu kỳ lượt đó ghép thêm được mục mới vào bảng màn hình. Đối chiếu payload với BẢNG ĐANG LƯU — thay vì
// với bảng server đã render — thì các mục mới ấy được "bù" vào bản chốt ở dạng TRẮNG và bị đóng dấu
// đã-duyệt trong khi người dùng chưa từng nhìn thấy chúng.
//
// Hỏng kiểu này KHÔNG báo lỗi ở đâu cả: nút gửi vẫn chạy, hội thoại vẫn có tin nhắn "mình đã rà bảng màn
// hình", chỉ có điều bảng đã chốt chở thêm thứ không ai rà và khối ngữ cảnh của nó thì cấm BA hỏi lại.
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
            // Phạm vi đã chắt từ hội thoại, chưa ai rà: ba dòng CHỜ DUYỆT.
            ScreenScopeMap = PendingTable(ScreenList, ScreenCreate, ScreenAssign)
        });
        db.SaveChanges();
    }

    // LỖI GỐC. Bảng render ra ba dòng; ngay sau đó một lượt chat nữa chạy và lượt chắt lọc ghép thêm hai
    // màn hình vừa lộ ra. Người dùng bấm gửi và phải nhận lại ĐÚNG ba dòng mình vừa điền — hai mục mới ở
    // lại bảng nhưng KHÔNG được đóng dấu, và bảng sẽ bày lại để hỏi chúng.
    [Fact]
    public async Task ExecuteAsync_KeepsTheReviewedTable_WhenTheDistillAddedRowsUnderneath()
    {
        await SeedRenderedTableAsync(ScreenList, ScreenCreate, ScreenAssign);
        await MergeIntoScopeAsync(
            "Màn hình lịch sử thay đổi JD",
            "Màn hình cấu hình mẫu JD chuẩn");

        var result = await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","purpose":"Quản lý và tra cứu danh sách",
              "functions":[{"name":"Xem danh sách JD","flowSteps":["JD được chuyển sang trạng thái có thể gán"],"included":true}],
              "included":true},
             {"screen":"{{ScreenCreate}}","purpose":"Cho manager tạo JD cho orgUnit của mình",
              "functions":[{"name":"Tạo JD","included":true},{"name":"Cập nhật JD","included":true},
                           {"name":"Gửi duyệt","flowSteps":["Manager submit JD"],"included":true}],
              "included":true},
             {"screen":"{{ScreenAssign}}","purpose":"Cho manager gán JD đã được duyệt",
              "functions":[{"name":"Chọn JD","included":true},
                           {"name":"Gán JD cho nhân viên","flowSteps":["Manager gán JD cho nhân viên"],"included":true}],
              "included":true}]
            """);

        Assert.Equal(3, result.Rows);

        var stored = ScreenScopeMapBuilder.Parse(await LoadScreenScopeAsync());
        // Ba dòng đã rà mang dấu; hai mục ghép thêm giữa chừng ở lại bảng và vẫn CHỜ DUYỆT.
        Assert.Equal(5, stored.Count);
        Assert.Equal(3, stored.Count(r => r.ConfirmedByUser));
        Assert.Equal(new[] { "Màn hình lịch sử thay đổi JD", "Màn hình cấu hình mẫu JD chuẩn" },
            ScreenScopeMapBuilder.PendingScreens(await LoadScreenScopeAsync()));
        // Phần đắt nhất của bảng là những ô người dùng tự điền — mất chúng thì bảng chỉ còn là danh sách tên.
        Assert.Equal("Quản lý và tra cứu danh sách", stored.Single(r => r.Screen == ScreenList).Purpose);
        Assert.Equal(new[] { "Tạo JD", "Cập nhật JD", "Gửi duyệt" },
            stored.Single(r => r.Screen == ScreenCreate).Functions.Select(f => f.Name));
        Assert.Equal("Manager gán JD cho nhân viên", Assert.Single(
            stored.Single(r => r.Screen == ScreenAssign).Functions.Single(f => f.Name == "Gán JD cho nhân viên").FlowSteps));

        // Và tin nhắn kể lại phải chở đúng chừng đó, vì mọi tầng chắt lọc đọc bản kể chứ không đọc cột DB.
        // TRỪ ô "việc của màn": nó được LƯU (bước sinh spec đọc nó) nhưng KHÔNG đi vào bản kể, vì bản kể được
        // lưu dưới vai NGƯỜI DÙNG còn ô đó là văn xuôi BA điền sẵn mà họ đọc như một cái nhãn — xem ghi chú
        // class của EntityMapBuilder cho ca thật và BAChatTableCaptionRuleTests cho luật.
        Assert.Contains($"- {ScreenCreate} [chức năng: Tạo JD, Cập nhật JD, Gửi duyệt]", result.Message);
        Assert.DoesNotContain("Cho manager tạo JD cho orgUnit của mình", result.Message);
        Assert.DoesNotContain("lịch sử thay đổi JD", result.Message);
    }

    // Bỏ tích một CHỨC NĂNG là một quyết định nhỏ hơn hẳn bỏ cả màn hình, và trước đây người dùng không có
    // cách nào ra quyết định đó: cột chức năng là một ô text, sửa tay thì không để lại dấu vết máy đọc
    // được. Nó phải sống sót qua đường lưu VÀ phải được kể lại — im lặng thì họ không có bằng chứng nào
    // cho thấy mình vừa loại đúng thứ định loại.
    [Fact]
    public async Task ExecuteAsync_KeepsFunctionLevelChoices_AndNamesTheOnesDropped()
    {
        await SeedRenderedTableAsync(ScreenList, ScreenCreate);

        var result = await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","functions":[{"name":"Xem danh sách JD","included":true}],"included":true},
             {"screen":"{{ScreenCreate}}","functions":[{"name":"Tạo JD","included":true},
                                                       {"name":"Xóa JD","included":false}],"included":true}]
            """);

        var stored = ScreenScopeMapBuilder.Parse(await LoadScreenScopeAsync());
        var functions = stored.Single(r => r.Screen == ScreenCreate).Functions;
        Assert.True(functions.Single(f => f.Name == "Tạo JD").Included);
        Assert.False(functions.Single(f => f.Name == "Xóa JD").Included);
        Assert.Contains($"Các chức năng mình KHÔNG cần: Xóa JD (ở {ScreenCreate})", result.Message);
    }

    // Bấm gửi là hành vi xác nhận CẢ BẢNG: mọi dòng mang dấu, kể cả dòng bị bỏ tích — dòng ấy ở lại làm
    // BIA để lượt chắt lọc sau không dựng lại được thứ người dùng vừa đóng. Và cổng đóng ngay sau đó, vì
    // không còn mục nào chờ duyệt.
    [Fact]
    public async Task ExecuteAsync_StampsEveryRow_AndKeepsTheUntickedOneAsATombstone()
    {
        await SeedRenderedTableAsync(ScreenList, ScreenCreate, ScreenAssign);

        await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","included":true},
             {"screen":"{{ScreenCreate}}","included":true},
             {"screen":"{{ScreenAssign}}","included":false}]
            """);

        var json = await LoadScreenScopeAsync();
        Assert.All(ScreenScopeMapBuilder.Parse(json), r => Assert.True(r.ConfirmedByUser));
        Assert.False(ScreenScopeMapBuilder.HasPending(json));

        // Lượt chắt lọc sau gặp lại đúng cái tên vừa bị loại: bia chặn nó lại.
        Assert.Null(ScreenScopeMapBuilder.Merge(json, new[] { new ScopeAddition { Screen = ScreenAssign } }));

        // Và phạm vi hiệu dụng — nguồn DÒNG của bảng phân quyền — không còn mục nào người dùng đã loại.
        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        Assert.Equal(new[] { ScreenList, ScreenCreate }, PermissionMatrixGate.EffectiveScreens(project));
    }

    // Dòng người dùng đã bỏ tích KHÔNG có mặt ở lượt bày lại (SeedRows lọc nó ra), nên ghi đè thẳng bảng
    // bằng payload là xoá mất tấm bia. Lần chốt thứ hai phải giữ lại phần bảng vừa bày không mang ra hỏi.
    [Fact]
    public async Task ExecuteAsync_KeepsTombstonesThatTheReshownTableDidNotCarry()
    {
        await SeedRenderedTableAsync(ScreenList, ScreenCreate, ScreenAssign);
        await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","included":true},
             {"screen":"{{ScreenCreate}}","included":true},
             {"screen":"{{ScreenAssign}}","included":false}]
            """);

        // Lượt bày LẠI chỉ mang hai dòng còn tích cộng một màn hình mới.
        await SeedRenderedTableAsync(ScreenList, ScreenCreate, "Màn hình lịch sử thay đổi JD");
        await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","included":true},
             {"screen":"{{ScreenCreate}}","included":true},
             {"screen":"Màn hình lịch sử thay đổi JD","included":true}]
            """);

        var stored = ScreenScopeMapBuilder.Parse(await LoadScreenScopeAsync());
        var tombstone = stored.Single(r => r.Screen == ScreenAssign);
        Assert.False(tombstone.Included);
        Assert.True(tombstone.ConfirmedByUser);
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
        Assert.DoesNotContain(stored, r => r.Screen.Contains("quản trị hệ thống"));
        // Hai dòng vừa rà mang dấu; dòng thứ ba của bảng đang lưu không được mang ra hỏi lượt này nên nó ở
        // lại nguyên trạng — CHỜ DUYỆT, không bị đóng dấu theo.
        Assert.Equal(new[] { ScreenList, ScreenCreate },
            stored.Where(r => r.ConfirmedByUser).Select(r => r.Screen));
        Assert.Equal(new[] { ScreenAssign }, ScreenScopeMapBuilder.PendingScreens(await LoadScreenScopeAsync()));
    }

    // …nhưng chốt chặn đó dựng để chặn MODEL, nên nó phải nhường đúng một chỗ: dòng người dùng TỰ THÊM bằng
    // nút "thêm màn hình". Không có ngoại lệ này thì cái nút chỉ là một trò đùa — họ gõ tên màn hình, bấm
    // gửi, và dòng ấy biến mất trong im lặng đúng như mọi dòng bịa khác.
    [Fact]
    public async Task ExecuteAsync_KeepsScreensTheUserAddedToTheTableThemselves()
    {
        await SeedRenderedTableAsync(ScreenList, ScreenCreate);

        var result = await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","included":true},
             {"screen":"{{ScreenCreate}}","included":true},
             {"screen":"Màn hình báo cáo JD theo phòng ban","purpose":"Xem thống kê JD đã gán",
              "functions":[{"name":"Xem báo cáo","included":true}],
              "addedByUser":true,"included":true}]
            """);

        Assert.Equal(3, result.Rows);

        // Dòng tự thêm xếp SAU CÙNG trong phần vừa gửi — đúng chỗ nó đứng trên bảng — và chở theo mọi ô
        // người dùng đã điền.
        var stored = ScreenScopeMapBuilder.Parse(await LoadScreenScopeAsync());
        var added = stored.Single(r => r.AddedByUser);
        Assert.Equal("Màn hình báo cáo JD theo phòng ban", added.Screen);
        Assert.Equal(added, stored.Last(r => r.ConfirmedByUser));
        Assert.Equal("Xem thống kê JD đã gán", added.Purpose);
        Assert.Equal("Xem báo cáo", Assert.Single(added.Functions).Name);
        Assert.True(added.AddedByUser);

        // Một màn hình chưa từng có trong đề xuất mà lặng lẽ đi vào phạm vi là đúng loại thay đổi phải nói
        // ra: mọi tầng chắt lọc phía sau đọc bản kể này chứ không đọc cột DB.
        Assert.Contains("Các màn hình mình tự bổ sung vào bảng: Màn hình báo cáo JD theo phòng ban.", result.Message);

        // Và nó phải vào được phạm vi hiệu dụng, nếu không thì bảng phân quyền ngay sau đó không có dòng nào
        // cho màn hình vừa thêm — tức mặc nhiên "không ai được xem".
        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        Assert.Contains("Màn hình báo cáo JD theo phòng ban", PermissionMatrixGate.EffectiveScreens(project));
    }

    // Cờ tự thêm KHÔNG phải một cửa sau cho mọi dòng: bấm "thêm màn hình" rồi bỏ trống ô tên thì không có
    // màn hình nào cả, và hai dòng tự thêm cùng tên chỉ là một.
    [Fact]
    public async Task ExecuteAsync_DropsBlankAndDuplicateRowsTheUserAdded()
    {
        await SeedRenderedTableAsync(ScreenList);

        var result = await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","included":true},
             {"screen":"   ","addedByUser":true,"included":true},
             {"screen":"Màn hình báo cáo JD theo phòng ban","addedByUser":true,"included":true},
             {"screen":"Màn hình báo cáo JD theo phòng ban","addedByUser":true,"included":true}]
            """);

        Assert.Equal(2, result.Rows);
    }

    // Bỏ tích SẠCH bảng: KHÔNG ghi gì và báo 0 dòng để UI giữ bảng lại. Bảng này là nguồn phạm vi duy
    // nhất, nên lưu một bảng trắng trơn là khóa chết cổng phân quyền — nó đòi phạm vi có mục mới mở — và
    // khóa trong im lặng: nút "Write Requirement" không bao giờ sáng, không gì trên màn hình nói vì sao.
    [Fact]
    public async Task ExecuteAsync_StoresNothing_WhenEveryRowWasUnticked()
    {
        await SeedRenderedTableAsync(ScreenList, ScreenCreate);
        var before = await LoadScreenScopeAsync();

        var result = await ExecuteAsync($$"""
            [{"screen":"{{ScreenList}}","included":false},
             {"screen":"{{ScreenCreate}}","included":false}]
            """);

        Assert.Equal(0, result.Rows);
        Assert.Equal(before, await LoadScreenScopeAsync());
    }

    // Không có lượt bảng nào để đối chiếu (lượt đã bị "New Chat" lưu trữ, hoặc dự án chốt bằng đường khác)
    // ⇒ quay về các dòng còn tích của bảng đang lưu. Fail-open: mất chốt chặn tên màn hình rẻ hơn nhiều so
    // với một nút gửi không bao giờ lưu được gì.
    [Fact]
    public async Task ExecuteAsync_FallsBackToTheStoredTable_WhenNoRenderedTableSurvives()
    {
        await SeedRenderedTableAsync(archived: true, screens: new[] { ScreenList, ScreenCreate });

        var result = await ExecuteAsync($$"""
            [{"screen":"{{ScreenAssign}}","purpose":"Cho manager gán JD","included":true}]
            """);

        Assert.Equal(3, result.Rows); // ba dòng của bảng đang lưu, hai dòng còn lại được bù vào
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
        Assert.False(ScreenScopeMapBuilder.IsConfirmed(await LoadScreenScopeAsync()));
    }

    // ==== dàn dựng ====

    private Task SeedRenderedTableAsync(params string[] screens) => SeedRenderedTableAsync(false, screens);

    /// <summary>Lượt BA BÀY BẢNG: bảng server render được lưu ở AgentConversation.ScreenScopeMap của lượt đó.</summary>
    private async Task SeedRenderedTableAsync(bool archived, string[] screens)
    {
        var rows = ScreenScopeMapBuilder.Build(
            null, screens.Select(s => new ScreenScopeRow { Screen = s }), screens);

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

    /// <summary>
    /// Lượt chắt lọc hậu kỳ ghép thêm màn hình vừa lộ ra — chạy sau lượt bày bảng, trước khi người dùng bấm
    /// gửi (một lượt chat nữa xen vào giữa).
    /// </summary>
    private async Task MergeIntoScopeAsync(params string[] screens)
    {
        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        var merged = ScreenScopeMapBuilder.Merge(
            project.ScreenScopeMap, screens.Select(s => new ScopeAddition { Screen = s }));
        project.ScreenScopeMap = System.Text.Json.JsonSerializer.Serialize(merged);
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

    /// <summary>Bảng màn hình đã chắt từ hội thoại nhưng CHƯA AI RÀ.</summary>
    private static string PendingTable(params string[] screens)
        => System.Text.Json.JsonSerializer.Serialize(
            screens.Select(s => new ScreenScopeRow { Screen = s, Included = true }).ToList());

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
