using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Luật ngôn ngữ của POC: nhãn giao diện luôn tiếng Anh, chỉ nội dung nghiệp vụ được tiếng Việt. Bất biến
// đáng giữ nhất là chiều NGƯỢC: dữ liệu mẫu tiếng Việt (ô bảng, option danh mục, nhãn checkbox mang tên
// giá trị, tiêu đề bản ghi) KHÔNG bao giờ bị bắt — một cổng bắt nhầm là cổng agent học cách phớt lờ.
public class PocUiLanguageCheckTests
{
    [Fact]
    public void EnglishChromeWithVietnameseContent_IsClean()
    {
        var html = Poc("""
            <section class="page-view" data-view="JD List">
              <h2>Chuyên viên tuyển dụng</h2>
              <input placeholder="Search by Employee ID or Full Name">
              <select><option value="">-- Select JD --</option><option value="1">Phòng Nhân sự</option></select>
              <label><input type="checkbox"> Khối Văn phòng</label>
              <table><tr><th>JD Code</th><th>Job Title</th></tr>
                <tr><td>HcP-JD-001</td><td>Chuyên viên tuyển dụng</td></tr></table>
              <button class="btn btn-primary">Create JD</button>
              <span class="badge">Pending HoD Approval</span>
            </section>
            """);

        Assert.Empty(PocUiLanguageCheck.Inspect(html, ["Catalogs", "Reports"]));
    }

    [Theory]
    [InlineData("<button>Tạo JD</button>", "Tạo JD")]
    [InlineData("<table><tr><th>Mã JD</th></tr></table>", "Mã JD")]
    [InlineData("<label class=\"form-label\">Ngày hiệu lực</label>", "Ngày hiệu lực")]
    [InlineData("<input placeholder=\"Tìm theo Employee ID\">", "Tìm theo Employee ID")]
    [InlineData("<span title=\"Đã có version mới đang chờ duyệt\">!</span>", "Đã có version mới")]
    [InlineData("<select><option value=\"\">-- Chọn JD --</option></select>", "Chọn JD")]
    [InlineData("<ul class=\"nav nav-tabs\"><li><a class=\"nav-link\">Chức danh</a></li></ul>", "Chức danh")]
    [InlineData("<span class=\"badge\">Chờ duyệt</span>", "Chờ duyệt")]
    public void VietnameseChrome_IsAnIssue_NamingTheLabel(string content, string expected)
    {
        var issues = PocUiLanguageCheck.Inspect(Poc(content));

        var issue = Assert.Single(issues);
        Assert.Contains(expected, issue);
        Assert.Contains("TIẾNG ANH", issue);
    }

    [Fact]
    public void VietnameseMenuGroupLabel_IsAnIssue()
    {
        var issue = Assert.Single(PocUiLanguageCheck.Inspect(Poc("<p>x</p>"), ["Danh mục"]));

        Assert.Contains("'Danh mục' (menu group)", issue);
    }

    [Fact]
    public void ManyOffenders_AreGatheredIntoOneIssue()
    {
        var issues = PocUiLanguageCheck.Inspect(Poc("<button>Hủy</button><button>Xác nhận</button><button>Hủy</button>"));

        var issue = Assert.Single(issues);
        Assert.Contains("2 chỗ", issue);
    }

    [Fact]
    public void ShellOutsideTheContentRegion_IsNotScanned()
    {
        // Shell là của template (có option "Tiếng Việt" trong ô chọn ngôn ngữ) — không phải bản demo sinh ra.
        var html = "<body><button title=\"Tiếng Việt\">Tiếng Việt</button>"
                   + PocTemplate.StartMarker + "<button>Save</button>" + PocTemplate.EndMarker + "</body>";

        Assert.Empty(PocUiLanguageCheck.Inspect(html));
    }

    private static string Poc(string content) =>
        "<html><body><nav class=\"sidebar-nav\"></nav>"
        + PocTemplate.StartMarker + content + PocTemplate.EndMarker
        + "</body></html>";
}
