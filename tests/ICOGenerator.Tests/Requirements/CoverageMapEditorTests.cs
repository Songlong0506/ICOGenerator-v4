using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Đường đính chính bản đồ bao phủ của NGƯỜI DÙNG ("chưa đúng?"). Điều quan trọng nhất: sau khi hạ nhóm,
// cổng Write Requirement phải ĐÓNG lại — nếu không thì nút vẫn mở và tài liệu vẫn được viết theo cách
// hiểu mà người dùng vừa nói là sai.
public class CoverageMapEditorTests
{
    private const string Map = """
        - ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép. {nguồn: "mình cần quản lý đơn nghỉ phép"}
        - ★ Đối tượng người dùng & vai trò: [RÕ] Nhân viên và quản lý.
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Gửi đơn → duyệt.
        - Quy mô sử dụng: [KHÔNG ÁP DỤNG] nội bộ nhỏ.
        """;

    [Fact]
    public void Reopen_DowngradesTargetLineAndKeepsPreviousSummary()
    {
        var updated = CoverageMapEditor.Reopen(Map, "Đối tượng người dùng & vai trò");

        Assert.NotNull(updated);
        var items = CoverageMapParser.Parse(updated);
        var target = items.Single(x => x.Label == "Đối tượng người dùng & vai trò");
        Assert.Equal("MỘT PHẦN", target.Status);
        Assert.Contains("người dùng báo phần này chưa đúng", target.Summary);
        Assert.Contains("Nhân viên và quản lý", target.Summary);
        // Các dòng khác không bị đụng tới.
        Assert.Equal("RÕ", items.Single(x => x.Label == "Mục tiêu / bài toán").Status);
        Assert.Equal(4, items.Count);
    }

    [Fact]
    public void Reopen_ClosesTheWriteRequirementGate()
    {
        Assert.True(RequirementReadinessGate.Evaluate(Map).Ready);

        var updated = CoverageMapEditor.Reopen(Map, "Chức năng & luồng nghiệp vụ chính");

        Assert.False(RequirementReadinessGate.Evaluate(updated).Ready);
    }

    [Fact]
    public void Reopen_KeepsCoreMarker()
    {
        var updated = CoverageMapEditor.Reopen(Map, "Mục tiêu / bài toán");

        Assert.True(CoverageMapParser.Parse(updated).Single(x => x.Label == "Mục tiêu / bài toán").IsCore);
    }

    [Fact]
    public void Reopen_UnknownLabelOrEmptyMap_ReturnsNull()
    {
        Assert.Null(CoverageMapEditor.Reopen(Map, "Nhóm không tồn tại"));
        Assert.Null(CoverageMapEditor.Reopen(null, "Mục tiêu / bài toán"));
        Assert.Null(CoverageMapEditor.Reopen(Map, ""));
    }

    [Fact]
    public void Parse_SplitsEvidenceOutOfSummary()
    {
        var item = CoverageMapParser.Parse(Map).Single(x => x.Label == "Mục tiêu / bài toán");

        Assert.Equal("Quản lý đơn nghỉ phép.", item.Summary);
        Assert.Equal("\"mình cần quản lý đơn nghỉ phép\"", item.Evidence);
    }

    [Fact]
    public void Parse_OldMapWithoutEvidence_StillParses()
    {
        var item = CoverageMapParser.Parse("- ★ Mục tiêu / bài toán: [RÕ] Quản lý kho.").Single();

        Assert.Equal("Quản lý kho.", item.Summary);
        Assert.Equal(string.Empty, item.Evidence);
    }
}
