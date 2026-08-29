using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Organization;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Organization;

// Cây orgUnit là NGUỒN DUY NHẤT của phép roll-up phòng ban: ghi chú "đơn vị yêu cầu" của BA, bucket
// checklist học được và bảng Usage đều hỏi nó. Ba bản đi lệch nhau nghĩa là cùng một dự án bị xếp vào hai
// phòng ban khác nhau tùy chỗ hỏi, nên các test ở đây chốt đúng phép đi ngược đó — kể cả với dữ liệu HR
// bẩn (chuỗi cấp trên đứt đoạn, tự trỏ về mình, tạo chu trình).
public class OrgChartTests
{
    private static OrgChart NewChart(params OrgUnitNode[] units) => new(units);

    [Fact]
    public void FindDepartment_WalksUpToNearestDepartment()
    {
        var chart = NewChart(
            new OrgUnitNode("D", "HcP/HRL", null, null, IsDepartment: true),
            new OrgUnitNode("M", "HcP/HRL1", "D", null, IsDepartment: false),
            new OrgUnitNode("L", "HcP/HRL1-A", "M", null, IsDepartment: false));

        Assert.Equal("D", chart.FindDepartment("L")?.Code);
        Assert.Equal("D", chart.FindDepartment("M")?.Code);
    }

    // Đơn vị đã LÀ department thì chính nó là câu trả lời — không đi tiếp lên trên.
    [Fact]
    public void FindDepartment_UnitIsAlreadyDepartment_ReturnsItself()
    {
        var chart = NewChart(
            new OrgUnitNode("TOP", "HcP", null, null, IsDepartment: true),
            new OrgUnitNode("D", "HcP/HRL", "TOP", null, IsDepartment: true));

        Assert.Equal("D", chart.FindDepartment("D")?.Code);
    }

    // Chuỗi cấp trên không dẫn tới department nào ⇒ null. Caller tự quyết fallback: bucket checklist rơi
    // về bucket chung, còn bảng Usage giữ chính orgUnit làm nhóm.
    [Fact]
    public void FindDepartment_NoDepartmentOnPath_ReturnsNull()
    {
        var chart = NewChart(
            new OrgUnitNode("A", "HcP/A", "B", null, IsDepartment: false),
            new OrgUnitNode("B", "HcP/B", null, null, IsDepartment: false));

        Assert.Null(chart.FindDepartment("A"));
    }

    // Dữ liệu HR bẩn không được làm treo vòng lặp.
    [Theory]
    [InlineData("A", "A")] // tự trỏ về mình
    [InlineData("A", "B")] // chu trình A → B → A
    public void FindDepartment_CyclicParents_TerminatesAndReturnsNull(string aParent, string bParent)
    {
        var chart = NewChart(
            new OrgUnitNode("A", "HcP/A", aParent, null, IsDepartment: false),
            new OrgUnitNode("B", "HcP/B", bParent, null, IsDepartment: false));

        Assert.Null(chart.FindDepartment("A"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("KHONG-CO-THAT")]
    public void Find_MissingOrBlankCode_ReturnsNull(string? code)
    {
        var chart = NewChart(new OrgUnitNode("D", "HcP/HRL", null, null, IsDepartment: true));

        Assert.Null(chart.Find(code));
        Assert.Null(chart.FindDepartment(code));
    }

    [Fact]
    public void Find_TrimsAndIgnoresCase()
    {
        var chart = NewChart(new OrgUnitNode("D50", "HcP/HRL", null, null, IsDepartment: true));

        Assert.Equal("D50", chart.Find("  d50 ")?.Code);
    }

    // Đồng bộ HR lỗi có thể đẻ ra hai dòng cùng mã — dựng cây không được ném lỗi (mọi đường gọi tới đây
    // đều là đường phụ trợ fail-open).
    [Fact]
    public void Constructor_DuplicateCodes_KeepsFirstWithoutThrowing()
    {
        var chart = NewChart(
            new OrgUnitNode("D", "Bản đầu", null, null, IsDepartment: true),
            new OrgUnitNode("d", "Bản trùng", null, null, IsDepartment: true));

        Assert.Equal("Bản đầu", chart.Find("D")?.DisplayName);
        Assert.Single(chart.Units);
    }

    // Provider bỏ đơn vị đã xóa mềm và đơn vị thiếu mã; DisplayName trống thì lấy chính mã làm nhãn.
    [Fact]
    public async Task Provider_SkipsDeletedAndCodelessUnits()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var seed = new AppDbContext(options, new PassthroughApiKeyProtector()))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "D", DisplayName = "HcP/HRL", IsDepartment = true });
            seed.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "GONE", DisplayName = "Đã xóa", IsDelete = true });
            seed.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "NONAME" });
            await seed.SaveChangesAsync();
        }

        await using var db = new AppDbContext(options, new PassthroughApiKeyProtector());
        var chart = await TestOrgChart.NewProvider(db).GetAsync();

        Assert.Equal(2, chart.Units.Count);
        Assert.Null(chart.Find("GONE"));
        Assert.Equal("NONAME", chart.Find("NONAME")?.DisplayName); // thiếu tên ⇒ dùng mã làm nhãn.
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }
}
