using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

public class PocCrossScreenConsistencyTests
{
    private static PocScreenTable Table(string screen, string[] headers, params string[][] rows) =>
        new(screen, headers, rows.Select(r => (IReadOnlyList<string>)r).ToList());

    [Fact]
    public void SameRecordWithDifferentValue_IsReported()
    {
        var tables = new[]
        {
            Table("Danh sách đơn", ["Nhân viên", "Số ngày", "Trạng thái"],
                ["Trần Văn Hùng", "3", "Chờ duyệt"]),
            Table("Duyệt đơn", ["Nhân viên", "Số ngày", "Trạng thái"],
                ["Trần Văn Hùng", "5", "Chờ duyệt"])
        };

        var issues = PocCrossScreenConsistency.Check(tables);

        Assert.Single(issues);
        Assert.Contains("Trần Văn Hùng", issues[0]);
        Assert.Contains("Số ngày", issues[0]);
    }

    [Fact]
    public void ConsistentData_ReportsNothing()
    {
        var tables = new[]
        {
            Table("Danh sách đơn", ["Nhân viên", "Số ngày"], ["Trần Văn Hùng", "3"]),
            Table("Duyệt đơn", ["Nhân viên", "Số ngày"], ["Trần Văn Hùng", "3"])
        };

        Assert.Empty(PocCrossScreenConsistency.Check(tables));
    }

    [Fact]
    public void FormattingDifferences_AreNotConflicts()
    {
        var tables = new[]
        {
            Table("Đơn hàng", ["Mã đơn", "Giá trị"], ["DH-001", "1.000.000 đ"]),
            Table("Thống kê", ["Mã đơn", "Giá trị"], ["DH-001", "1,000,000đ"])
        };

        Assert.Empty(PocCrossScreenConsistency.Check(tables));
    }

    [Fact]
    public void RepeatedKeyWithinOneTable_IsNotAnIdentity()
    {
        // Một khách hàng có nhiều đơn: khoá lặp trong cùng bảng KHÔNG phải định danh bản ghi, nên các giá
        // trị khác nhau ở màn khác là bình thường — cổng này không được báo động giả ở đây.
        var tables = new[]
        {
            Table("Đơn hàng", ["Khách hàng", "Giá trị"],
                ["Công ty ABC", "500.000"],
                ["Công ty ABC", "800.000"]),
            Table("Công nợ", ["Khách hàng", "Giá trị"], ["Công ty ABC", "1.300.000"])
        };

        Assert.Empty(PocCrossScreenConsistency.Check(tables));
    }

    [Fact]
    public void ActionColumnAndEmptyCells_AreIgnored()
    {
        var tables = new[]
        {
            Table("Danh sách", ["Mã", "Thao tác", "Ghi chú"], ["SP-001", "Sửa | Xoá", ""]),
            Table("Chi tiết", ["Mã", "Thao tác", "Ghi chú"], ["SP-001", "Xem", "Hàng mới"])
        };

        Assert.Empty(PocCrossScreenConsistency.Check(tables));
    }

    [Fact]
    public void SingleTable_ReportsNothing()
    {
        var tables = new[] { Table("Danh sách", ["Mã", "Giá"], ["SP-001", "10"], ["SP-002", "20"]) };

        Assert.Empty(PocCrossScreenConsistency.Check(tables));
    }
}
