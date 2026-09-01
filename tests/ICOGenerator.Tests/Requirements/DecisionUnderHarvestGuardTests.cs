using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Nhật ký "Điều đã chốt" bỏ sót thì KHÔNG có triệu chứng nào trên màn hình — nó không còn mặt UI nào.
// Thứ phát hiện ra là bộ đọc thứ hai: bản đồ bao phủ đọc ĐÚNG các lượt đó bằng một lời gọi khác, và mọi
// dòng [RÕ]/[MỘT PHẦN] đều phải kèm {nguồn: …} trích ngắn lời người dùng. Một trích dẫn của bản đồ nằm
// trong lời người dùng của lô ⇒ lô có nội dung nghiệp vụ; nhật ký không đổi trong khi đó ⇒ nghi bỏ sót.
//
// Ca thật đứng sau (ghi ngay trong decision-log.v1.md, dự án JD Libary 5): sau 26 lượt — người dùng đã
// chốt vai trò nào gán JD, bộ trường của một JD, việc bỏ ngày hết hạn, việc không cần báo cáo và quy mô
// sử dụng — nhật ký chỉ có ĐÚNG MỘT dòng, và RequirementConflictService (soát mâu thuẫn bằng chính danh
// sách này) mù suốt buổi đó.
public class DecisionUnderHarvestGuardTests
{
    private static readonly string MapWithEvidence =
        CoverageMapFixture.Map("- ★ Mục tiêu / bài toán: [RÕ] App quản lý danh sách JD trong nhà máy. {nguồn: \"đây là app để quản lý danh sách JD ở trong nhà máy\"}\n"
        + "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] HRBP gán JD. còn thiếu: mỗi vai trò làm/xem được gì {nguồn: \"HRBP là người gán JD cho nhân viên\"}\n"
        + "- Báo cáo / thống kê: [CHƯA HỎI]");

    private static readonly string[] BatchUserTurns =
    {
        "Đây là app để quản lý danh sách JD ở trong nhà máy, thay cho 2 file Excel.",
        "HRBP là người gán JD cho nhân viên."
    };

    [Fact]
    public void EvidenceFromBatch_AndLogUnchanged_SuspectsMiss()
    {
        var result = DecisionUnderHarvestGuard.Check(
            MapWithEvidence, BatchUserTurns, "- Nhật ký cũ một dòng", "- Nhật ký cũ một dòng");

        Assert.True(result.SuspectsMiss);
        // Guard trả về ĐÚNG các câu đã khớp — lượt chắt lại được chỉ thẳng vào chỗ vừa bỏ qua, không phải
        // một lời "cố lên" chung chung.
        Assert.Equal(2, result.Evidence.Count);
        Assert.Contains(result.Evidence, x => x.Contains("quản lý danh sách JD ở trong nhà máy"));
        Assert.Contains(result.Evidence, x => x.Contains("HRBP là người gán JD"));
    }

    [Fact]
    public void LogGrew_DoesNotSuspect()
    {
        var result = DecisionUnderHarvestGuard.Check(
            MapWithEvidence, BatchUserTurns,
            "- Nhật ký cũ một dòng",
            "- Nhật ký cũ một dòng\n- HRBP gán JD cho nhân viên trong nhà máy.");

        Assert.False(result.SuspectsMiss);
    }

    [Fact]
    public void LogLineRewritten_CountsAsChanged_DoesNotSuspect()
    {
        // Chắt lọc có thể SỬA một dòng cũ (người dùng đổi ý) mà không thêm dòng nào — vẫn là có làm việc.
        var result = DecisionUnderHarvestGuard.Check(
            MapWithEvidence, BatchUserTurns,
            "- HRBP gán JD.",
            "- HRBP gán JD cho nhân viên; Manager chỉ xem.");

        Assert.False(result.SuspectsMiss);
    }

    [Fact]
    public void EvidenceNotFromThisBatch_DoesNotSuspect()
    {
        // Bản đồ đầy bằng chứng, nhưng không câu nào lấy từ lô đang xét (lô này người dùng chỉ hỏi lại) ⇒
        // im lặng. Đây là chiều an toàn: guard chỉ nói khi có căn cứ trong CHÍNH lô.
        var result = DecisionUnderHarvestGuard.Check(
            MapWithEvidence,
            new[] { "Mình chưa hiểu câu hỏi của bạn, ý bạn là gì?" },
            "- Nhật ký cũ", "- Nhật ký cũ");

        Assert.False(result.SuspectsMiss);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public void EvidenceThatIsADescription_NotAQuote_DoesNotMatch()
    {
        // {nguồn: bảng phân quyền người dùng đã chốt} là MÔ TẢ hợp lệ, không phải câu người dùng nói —
        // nó không nằm trong lượt nào nên không được dùng làm căn cứ nghi ngờ.
        var map = CoverageMapFixture.Map("- Phân quyền theo nghiệp vụ: [RÕ] Bốn vai trò. {nguồn: bảng phân quyền người dùng đã chốt}");

        var result = DecisionUnderHarvestGuard.Check(
            map, BatchUserTurns, "- Nhật ký cũ", "- Nhật ký cũ");

        Assert.False(result.SuspectsMiss);
    }

    [Fact]
    public void ShortEvidence_IsIgnored()
    {
        // "có" / "tất cả" trùng nhau ở mọi buổi phỏng vấn — dưới ngưỡng thì không kết luận gì.
        var map = CoverageMapFixture.Map("- Quy mô sử dụng: [RÕ] Trên 100 người. {nguồn: \"có\"}");

        var result = DecisionUnderHarvestGuard.Check(
            map, new[] { "Có, bên mình có khoảng 100 người dùng ứng dụng này." },
            "- Nhật ký cũ", "- Nhật ký cũ");

        Assert.False(result.SuspectsMiss);
    }

    [Fact]
    public void PunctuationAndSpacingDifferences_StillMatch()
    {
        // Model trích lại gần đúng chứ hiếm khi đúng từng ký tự: khác dấu phẩy, khác khoảng trắng, khác
        // hoa thường thì vẫn phải khớp — nếu không guard sẽ im lặng đúng lúc cần nói.
        var map = CoverageMapFixture.Map("- ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Quản lý duyệt là xong. {nguồn: \"quản lý duyệt xong là đơn khoá luôn,\"}");

        var result = DecisionUnderHarvestGuard.Check(
            map,
            new[] { "Quản   lý duyệt xong  là đơn khoá luôn — không sửa được nữa." },
            "- Nhật ký cũ", "- Nhật ký cũ");

        Assert.True(result.SuspectsMiss);
    }

    [Fact]
    public void EmptyMapOrNoEvidence_DoesNotSuspect()
    {
        Assert.False(DecisionUnderHarvestGuard.Check(null, BatchUserTurns, "- A", "- A").SuspectsMiss);
        Assert.False(DecisionUnderHarvestGuard.Check("", BatchUserTurns, "- A", "- A").SuspectsMiss);
        // Bản đồ toàn [CHƯA HỎI] ⇒ không dòng nào có khối {nguồn: …}.
        Assert.False(DecisionUnderHarvestGuard
            .Check("- ★ Mục tiêu / bài toán: [CHƯA HỎI]", BatchUserTurns, "- A", "- A").SuspectsMiss);
    }

    [Fact]
    public void NoUserTurnsInBatch_DoesNotSuspect()
    {
        // Lô chỉ có lượt BA (không có gì để chốt) ⇒ nhật ký không đổi là ĐÚNG, không phải bỏ sót.
        var result = DecisionUnderHarvestGuard.Check(
            MapWithEvidence, Array.Empty<string>(), "- Nhật ký cũ", "- Nhật ký cũ");

        Assert.False(result.SuspectsMiss);
    }

    [Fact]
    public void Unchanged_IgnoresOrderAndWhitespace_ButSeesRealEdits()
    {
        Assert.True(DecisionUnderHarvestGuard.Unchanged("- A dài hơn\n- B dài hơn", "- B dài hơn\n-   A dài hơn "));
        Assert.True(DecisionUnderHarvestGuard.Unchanged(null, ""));
        Assert.False(DecisionUnderHarvestGuard.Unchanged("- A dài hơn", "- A dài hơn\n- B dài hơn"));
        Assert.False(DecisionUnderHarvestGuard.Unchanged("- A dài hơn", "- A dài hơn nữa"));
    }
}
