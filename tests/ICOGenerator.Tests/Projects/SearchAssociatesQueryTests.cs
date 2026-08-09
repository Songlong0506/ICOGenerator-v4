using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Ô gợi ý "Gửi cho ai" của hộp thoại chia sẻ bản demo. Test bám vào đúng những thứ quyết định ô này có
// dùng được không: từ khóa quá ngắn không đổ cả danh bạ ra, người đã nghỉ/đã xóa không hiện, tìm được
// bằng cả email lẫn NTID, và khớp từ đầu tên đứng trước.
public class SearchAssociatesQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public SearchAssociatesQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Associates.AddRange(
            Associate("Le Anh Hao", "HAO.LEANH@VN.BOSCH.COM", "LHN9HC"),
            Associate("Nguyen Thi Le", "LE.NGUYENTHI@VN.BOSCH.COM", "NTL1AB"),
            Associate("Tran Van Nghi", "NGHI.TRANVAN@VN.BOSCH.COM", "TVN2CD"),
            Associate("Pham Da Nghi", "NGHI.PHAMDA@VN.BOSCH.COM", "PDN3EF", leavingDate: DateTime.UtcNow.AddDays(-30)),
            Associate("Vo Thi Nghi", "NGHI.VOTHI@VN.BOSCH.COM", "VTN4GH", isDelete: true));
        db.SaveChanges();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("l")]
    [InlineData(null)]
    public async Task ShortKeyword_ReturnsNothing(string? keyword)
    {
        Assert.Empty(await new SearchAssociatesQuery(NewDb()).ExecuteAsync(keyword));
    }

    [Fact]
    public async Task Matches_ByName_Email_Or_UserId_IgnoringCase()
    {
        var query = new SearchAssociatesQuery(NewDb());

        Assert.Equal("Le Anh Hao", Assert.Single(await query.ExecuteAsync("anh hao")).DisplayName);
        Assert.Equal("Le Anh Hao", Assert.Single(await query.ExecuteAsync("hao.leanh")).DisplayName);
        Assert.Equal("Le Anh Hao", Assert.Single(await query.ExecuteAsync("lhn9hc")).DisplayName);
    }

    [Fact]
    public async Task Excludes_LeaversAndDeletedRows()
    {
        // Ba người tên "Nghi" nhưng chỉ một người còn làm việc.
        var results = await new SearchAssociatesQuery(NewDb()).ExecuteAsync("nghi");

        Assert.Equal(["Tran Van Nghi"], results.Select(r => r.DisplayName));
    }

    [Fact]
    public async Task PrefixMatches_ComeFirst()
    {
        var results = await new SearchAssociatesQuery(NewDb()).ExecuteAsync("le");

        // "Le Anh Hao" khớp từ đầu tên; "Nguyen Thi Le" chỉ khớp ở cuối.
        Assert.Equal(["Le Anh Hao", "Nguyen Thi Le"], results.Select(r => r.DisplayName));
    }

    [Fact]
    public async Task ReturnsContactFields_ForRecognisingThePerson()
    {
        var person = Assert.Single(await new SearchAssociatesQuery(NewDb()).ExecuteAsync("anh hao"));

        Assert.Equal("HAO.LEANH@VN.BOSCH.COM", person.Email);
        Assert.Equal("PS/EPC2-VN", person.OrganizationUnit);
        Assert.Equal("Technical Documentation Engineer", person.Position);
    }

    private static Associate Associate(
        string displayName, string email, string userId, DateTime? leavingDate = null, bool isDelete = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            Email = email,
            UserId = userId,
            OrganizationUnit = "PS/EPC2-VN",
            Position = "Technical Documentation Engineer",
            LeavingDate = leavingDate,
            IsDelete = isDelete
        };

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
