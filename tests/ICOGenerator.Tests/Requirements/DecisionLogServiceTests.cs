using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

public class DecisionLogServiceTests
{
    [Fact]
    public void ParseItems_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(DecisionLogService.ParseItems(null));
        Assert.Empty(DecisionLogService.ParseItems("  "));
    }

    [Fact]
    public void ParseItems_ReadsBulletsAndSkipsProse()
    {
        var log = """
            - Ứng dụng quản lý đơn nghỉ phép cho ~50 nhân viên
            (ghi chú lạc loài của model)
            - Quản lý duyệt xong thì đơn hoàn tất
            """;

        var items = DecisionLogService.ParseItems(log);

        Assert.Equal(2, items.Count);
        Assert.Equal("Ứng dụng quản lý đơn nghỉ phép cho ~50 nhân viên", items[0]);
        Assert.Equal("Quản lý duyệt xong thì đơn hoàn tất", items[1]);
    }
}
