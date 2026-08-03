using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Trí nhớ "giả định đã được user duyệt đúng" — thứ quyết định cổng xác nhận giả định còn phải HỎI gì ở
// lượt sinh spec tiếp theo. Bất biến quan trọng nhất: LLM sinh lại hay đổi vặt dấu câu/khoảng trắng, mà
// một dấu chấm thừa không được phép biến câu đã duyệt thành "giả định mới cần rà lại".
public class AssumptionMemoryTests
{
    [Theory]
    [InlineData("Mỗi nhân viên chỉ thuộc một phòng ban", "Mỗi nhân viên chỉ thuộc một phòng ban.")]
    [InlineData("Hạn chót nhập theo ngày", "hạn chót   nhập theo ngày")]
    [InlineData("Có ba mức ưu tiên", "**Có ba mức ưu tiên**")]
    public void SelectUnconfirmed_TreatsCosmeticRewordingAsAlreadyConfirmed(string stored, string regenerated)
    {
        Assert.Empty(AssumptionMemory.SelectUnconfirmed(new[] { regenerated }, stored));
    }

    [Fact]
    public void SelectUnconfirmed_KeepsAssumptionsThatActuallyChanged()
    {
        var pending = AssumptionMemory.SelectUnconfirmed(
            new[] { "Mỗi nhân viên chỉ thuộc một phòng ban", "Đơn đã duyệt thì không sửa được" },
            "Mỗi nhân viên chỉ thuộc một phòng ban");

        Assert.Equal(new[] { "Đơn đã duyệt thì không sửa được" }, pending);
    }

    [Fact]
    public void SelectUnconfirmed_WithNoMemory_ReturnsEverything()
    {
        Assert.Equal(2, AssumptionMemory.SelectUnconfirmed(new[] { "A", "B" }, null).Count);
    }

    [Fact]
    public void Remember_AppendsWithoutDuplicating()
    {
        var stored = AssumptionMemory.Remember("Giả định A", new[] { "Giả định A.", "Giả định B" });

        Assert.Equal(new[] { "Giả định A", "Giả định B" }, AssumptionMemory.Split(stored));
    }

    [Fact]
    public void Remember_DropsRejectedItems_SoTheyGetAskedAgain()
    {
        var stored = AssumptionMemory.Remember("Giả định A\nGiả định B", new[] { "Giả định C" },
            rejected: new[] { "giả định a" });

        Assert.Equal(new[] { "Giả định B", "Giả định C" }, AssumptionMemory.Split(stored));
        Assert.Single(AssumptionMemory.SelectUnconfirmed(new[] { "Giả định A", "Giả định B" }, stored));
    }

    [Fact]
    public void Remember_WithNothingLeft_ReturnsNull()
    {
        Assert.Null(AssumptionMemory.Remember("Giả định A", null, rejected: new[] { "Giả định A" }));
        Assert.Null(AssumptionMemory.Remember(null, Array.Empty<string>()));
    }

    [Fact]
    public void Remember_CapsLengthByDroppingOldestWholeLines()
    {
        var old = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"Giả định cũ số {i} " + new string('x', 40)));

        var stored = AssumptionMemory.Remember(old, new[] { "Giả định mới nhất" });
        var lines = AssumptionMemory.Split(stored);

        Assert.True(stored!.Length <= 6000);
        // Cắt theo DÒNG TRỌN VẸN và giữ phần mới nhất: cắt giữa dòng đẻ ra khoá rác không khớp gì cả.
        Assert.Equal("Giả định mới nhất", lines[^1]);
        Assert.All(lines, l => Assert.DoesNotContain("\n", l));
        Assert.Empty(AssumptionMemory.SelectUnconfirmed(new[] { "Giả định mới nhất" }, stored));
    }
}
