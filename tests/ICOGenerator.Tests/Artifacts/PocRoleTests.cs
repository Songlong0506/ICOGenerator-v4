using System.Text.Json;
using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

/// <summary>
/// Tham số 'roles' của SetPocContent dựng khối VIEW AS — bộ chuyển vai thay cho màn đăng nhập giả. Parser
/// cố tình dễ tính như <see cref="PocNavItem"/>: model gửi mảng, gửi chuỗi phân cách bằng dấu phẩy hay
/// gửi cả mảng bọc trong chuỗi JSON đều phải ra cùng một danh sách — bỏ im lặng nghĩa là sidebar không
/// có gì để bấm mà agent vẫn tin là đã có.
/// </summary>
public class PocRoleTests
{
    private static List<string> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return PocRole.ParseList(doc.RootElement);
    }

    [Fact]
    public void ParseList_ReadsPlainArray()
    {
        Assert.Equal(new[] { "Nhân viên", "Quản lý" }, Parse("""["Nhân viên","Quản lý"]"""));
    }

    [Fact]
    public void ParseList_ReadsObjectsAndAliases()
    {
        Assert.Equal(new[] { "HRBP", "HoD" }, Parse("""[{"label":"HRBP"},{"role":"HoD"}]"""));
    }

    [Fact]
    public void ParseList_UnwrapsDoubleEncodedArray_AndCommaSeparatedString()
    {
        Assert.Equal(new[] { "Employee", "Manager" }, Parse("""  "[\"Employee\",\"Manager\"]"  """.Trim()));
        Assert.Equal(new[] { "Employee", "Manager" }, Parse("""  "Employee, Manager"  """.Trim()));
    }

    [Fact]
    public void ParseList_DropsDuplicatesAndBlanks_AndCapsTheList()
    {
        Assert.Equal(new[] { "Admin" }, Parse("""["Admin","  ","admin"]"""));
        Assert.Equal(PocRole.MaxRoles, Parse("""["r1","r2","r3","r4","r5","r6","r7","r8","r9","r10"]""").Count);
    }

    [Fact]
    public void ParseList_ReturnsEmpty_ForUnusableInput()
    {
        Assert.Empty(Parse("42"));
        Assert.Empty(Parse("""{"label":"Manager"}"""));
    }
}
