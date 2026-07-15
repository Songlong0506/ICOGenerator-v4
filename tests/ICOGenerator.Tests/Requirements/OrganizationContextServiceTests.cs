using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bối cảnh tổ chức render từ OrgUnits/Associates cho prompt BA. Các test chốt:
// (1) department render đủ HoD + số orgUnit trực thuộc + headcount roll-up cả cây con;
// (2) nhân sự đã nghỉ (LeavingDate quá khứ) / bản ghi IsDelete không được đếm;
// (3) dữ liệu cha-con có CHU TRÌNH không làm treo; (4) bảng trống ⇒ null (fail-open);
// (5) bản render được cache — dữ liệu đổi sau đó không làm đổi kết quả trong cùng cache;
// (6) ghi chú đơn vị yêu cầu: orgUnit con chỉ ra manager + department cha + HoD; mã lạ ⇒ null.
public class OrganizationContextServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    private const string DeptCode = "100";
    private const string SubUnitCode = "110";
    private const string LeafUnitCode = "111";

    public OrganizationContextServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task BuildBaContext_RendersDepartment_WithHod_SubUnits_AndRolledUpHeadcount()
    {
        await SeedOrgTreeAsync();

        await using var db = NewDb();
        var context = await NewSut(db).BuildBaContextAsync();

        Assert.NotNull(context);
        // Department + HoD (tra TrgtManagerLId → Associates.PersonalNumber) trên cùng một dòng.
        Assert.Contains("HcP/TEF (mã 100) — HoD: Tran Van Hod — 2 orgUnit trực thuộc", context);
        // Headcount roll-up: 1 (dept) + 2 (sub) + 1 (leaf) = 4 người đang hoạt động; người đã nghỉ
        // và bản ghi IsDelete không được đếm.
        Assert.Contains("~4 nhân sự internal", context);
        // Chức danh phổ biến là dữ liệu gộp từ Associates đang hoạt động.
        Assert.Contains("Operator (2)", context);
        // Template placeholder phải được thay hết.
        Assert.DoesNotContain("{{DEPARTMENTS}}", context);
        Assert.DoesNotContain("{{POSITIONS}}", context);
        Assert.DoesNotContain("{{TOTALS}}", context);
    }

    [Fact]
    public async Task BuildBaContext_WhenOrgUnitsEmpty_ReturnsNull()
    {
        await using var db = NewDb();
        Assert.Null(await NewSut(db).BuildBaContextAsync());
    }

    [Fact]
    public async Task BuildBaContext_WithParentCycle_DoesNotHang()
    {
        await using (var db = NewDb())
        {
            // Hai orgUnit trỏ cấp trên vào nhau + một department trỏ vào chính nó — dữ liệu bẩn kiểu xấu nhất.
            db.OrgUnits.Add(NewUnit("A", "Cycle/A", parent: "B", isDepartment: true));
            db.OrgUnits.Add(NewUnit("B", "Cycle/B", parent: "A"));
            db.OrgUnits.Add(NewUnit("C", "Cycle/Self", parent: "C", isDepartment: true));
            await db.SaveChangesAsync();
        }

        await using var sutDb = NewDb();
        var context = await NewSut(sutDb).BuildBaContextAsync();

        Assert.NotNull(context);
        Assert.Contains("Cycle/A", context);
        Assert.Contains("Cycle/Self", context);
    }

    [Fact]
    public async Task BuildBaContext_IsCachedAcrossServiceInstances_UntilExpiry()
    {
        await SeedOrgTreeAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());

        await using (var db = NewDb())
        {
            var first = await NewSut(db, cache).BuildBaContextAsync();
            Assert.Contains("HcP/TEF", first);
        }

        // Đổi dữ liệu sau lần render đầu — bản trong cache (chia sẻ giữa các scoped instance) phải giữ nguyên.
        await using (var db = NewDb())
        {
            var dept = await db.OrgUnits.FirstAsync(u => u.OrgUnitCode == DeptCode);
            dept.DisplayName = "HcP/RENAMED";
            await db.SaveChangesAsync();
        }

        await using (var db2 = NewDb())
        {
            var second = await NewSut(db2, cache).BuildBaContextAsync();
            Assert.Contains("HcP/TEF", second);
            Assert.DoesNotContain("HcP/RENAMED", second);
        }
    }

    [Fact]
    public async Task BuildProjectUnitNote_ForChildUnit_NamesManager_AndParentDepartmentHod()
    {
        await SeedOrgTreeAsync();

        await using var db = NewDb();
        var note = await NewSut(db).BuildProjectUnitNoteAsync(LeafUnitCode);

        Assert.NotNull(note);
        Assert.Contains("OrgUnit HcP/TEF3.3 (mã 111) — manager: Le Thi Manager.", note);
        Assert.Contains("Thuộc department HcP/TEF — HoD: Tran Van Hod.", note);
    }

    [Fact]
    public async Task BuildProjectUnitNote_ForDepartmentItself_UsesHodWording()
    {
        await SeedOrgTreeAsync();

        await using var db = NewDb();
        var note = await NewSut(db).BuildProjectUnitNoteAsync(DeptCode);

        Assert.NotNull(note);
        Assert.Contains("HcP/TEF (mã 100) là một department — HoD: Tran Van Hod.", note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("khong-ton-tai")]
    public async Task BuildProjectUnitNote_WithMissingOrUnknownCode_ReturnsNull(string? code)
    {
        await SeedOrgTreeAsync();

        await using var db = NewDb();
        Assert.Null(await NewSut(db).BuildProjectUnitNoteAsync(code));
    }

    // Cây mẫu: department 100 (HcP/TEF, HoD 900) → 110 (HcP/TEF3, manager không có hồ sơ) → 111
    // (HcP/TEF3.3, manager 901). Nhân sự: 1 ở dept, 2 ở 110 (một người đã nghỉ KHÔNG đếm), 1 ở 111,
    // 1 bản ghi IsDelete KHÔNG đếm, cộng thêm chính HoD/manager đứng ở đơn vị 110.
    private async Task SeedOrgTreeAsync()
    {
        await using var db = NewDb();
        db.OrgUnits.Add(NewUnit(DeptCode, "HcP/TEF", parent: null, isDepartment: true, managerId: "900"));
        db.OrgUnits.Add(NewUnit(SubUnitCode, "HcP/TEF3", parent: DeptCode));
        db.OrgUnits.Add(NewUnit(LeafUnitCode, "HcP/TEF3.3", parent: SubUnitCode, managerId: "901"));
        // OrgUnit đã xóa mềm không được render và không nhận headcount.
        db.OrgUnits.Add(NewUnit("999", "HcP/DELETED", parent: DeptCode, isDelete: true));

        db.Associates.AddRange(
            NewAssociate("900", "Tran Van Hod", unitCode: DeptCode, position: "Head of Department"),
            NewAssociate("901", "Le Thi Manager", unitCode: SubUnitCode, position: "Operator"),
            NewAssociate("902", "Nguyen Van A", unitCode: SubUnitCode, position: "Operator"),
            NewAssociate("903", "Nguyen Van B", unitCode: LeafUnitCode, position: "Technician"),
            NewAssociate("904", "Da Nghi Viec", unitCode: SubUnitCode, position: "Operator",
                leavingDate: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewAssociate("905", "Da Bi Xoa", unitCode: LeafUnitCode, position: "Operator", isDelete: true));
        await db.SaveChangesAsync();
    }

    private static OrgUnit NewUnit(string code, string name, string? parent, bool isDepartment = false,
        string? managerId = null, bool isDelete = false) => new()
    {
        Id = Guid.NewGuid(),
        OrgUnitCode = code,
        DisplayName = name,
        TargetResponsible = parent,
        TrgtManagerLId = managerId,
        IsDepartment = isDepartment,
        IsDelete = isDelete
    };

    private static Associate NewAssociate(string personalNumber, string name, string unitCode, string position,
        DateTime? leavingDate = null, bool isDelete = false) => new()
    {
        Id = Guid.NewGuid(),
        PersonalNumber = personalNumber,
        DisplayName = name,
        OrgUnitCode = unitCode,
        Position = position,
        LeavingDate = leavingDate,
        IsDelete = isDelete
    };

    private static OrganizationContextService NewSut(AppDbContext db, IMemoryCache? cache = null) =>
        new(db, new StubPrompts(), cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OrganizationContextService>.Instance);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Template thật nằm ở Prompts/BusinessAnalyst/organization-context.v2.md (không copy sang test project); stub giữ
    // đúng ba placeholder để test khẳng định chúng được thay bằng dữ liệu render.
    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## Bối cảnh tổ chức\n\n{{DEPARTMENTS}}\n\n{{POSITIONS}}\n\n{{TOTALS}}";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
