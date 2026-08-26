using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cửa sổ hội thoại gửi kèm lượt soạn/soát/sửa Product Brief. Bất biến quan trọng nhất và là thứ dễ làm
// hỏng nhất khi sửa sau này: KHÔNG BAO GIỜ cắt quá con trỏ tóm tắt — cắt xa hơn là làm bốc hơi thông tin
// (phần bị bỏ không nằm trong tóm tắt, không nằm trong transcript, vòng tự soát mất luôn thứ phải đối chiếu).
public class BriefContextWindowTests
{
    private static List<int> Turns(int count, int length) => Enumerable.Repeat(length, count).ToList();

    [Fact]
    public void ComputeSkip_ShortConversation_KeepsEverything()
    {
        Assert.Equal(0, BriefContextWindow.ComputeSkip(Turns(10, 200), summarizedTurnCount: 0, approvedTurnCount: 0));
        Assert.Equal(0, BriefContextWindow.ComputeSkip(new List<int>(), summarizedTurnCount: 5, approvedTurnCount: 5));
    }

    [Fact]
    public void ComputeSkip_NeverCutsBeyondSummaryPointer()
    {
        // Hội thoại 200 lượt nhưng CHƯA tóm tắt lượt nào ⇒ vẫn gửi nguyên văn tất cả: thà prompt dài còn
        // hơn bỏ lượt mà không nơi nào chở lại. (Tóm tắt lỗi/chưa tới ngưỡng đều rơi vào nhánh này.)
        Assert.Equal(0, BriefContextWindow.ComputeSkip(Turns(200, 500), summarizedTurnCount: 0, approvedTurnCount: 0));

        // Muốn cắt tới mốc duyệt (lượt 90) nhưng tóm tắt mới tới lượt 50 ⇒ chỉ được cắt 50.
        Assert.Equal(50, BriefContextWindow.ComputeSkip(Turns(100, 300), summarizedTurnCount: 50, approvedTurnCount: 90));
    }

    [Fact]
    public void ComputeSkip_KeepsRecentWindow_WhenSummaryIsAhead()
    {
        // 100 lượt, tóm tắt đã tới lượt 75 ⇒ giữ nguyên văn 40 lượt cuối (trần số lượt).
        Assert.Equal(60, BriefContextWindow.ComputeSkip(Turns(100, 300), summarizedTurnCount: 75, approvedTurnCount: 0));
    }

    [Fact]
    public void ComputeSkip_ApprovedMark_CutsMoreThanTurnWindow()
    {
        // Mốc duyệt (lượt 80) mới hơn cửa sổ 40 lượt: phần trước mốc đã được chính bản Brief ĐÃ DUYỆT chở,
        // nên được cắt thêm — đây là chỗ (3) thật sự tiết kiệm so với chỉ có (2).
        Assert.Equal(80, BriefContextWindow.ComputeSkip(Turns(100, 300), summarizedTurnCount: 85, approvedTurnCount: 80));
    }

    [Fact]
    public void ComputeSkip_CutsByChars_WhenFewTurnsAreHuge()
    {
        // 10 lượt × 20.000 ký tự = 200.000: lọt trần SỐ LƯỢT nhưng thừa xa trần ký tự. Đếm lượt một mình
        // không chặn được token vì một lượt chốt bảng dài bằng vài chục lượt hỏi đáp.
        var skip = BriefContextWindow.ComputeSkip(Turns(10, 20_000), summarizedTurnCount: 10, approvedTurnCount: 0);

        Assert.True(skip > 0);
        Assert.True((10 - skip) * 20_000 <= BriefContextWindow.MaxVerbatimChars);
    }

    [Fact]
    public void ComputeSkip_SingleHugeTurn_IsNeverCutAway()
    {
        // Một lượt dài hơn cả trần ⇒ dừng ở chính nó thay vì bỏ sạch hội thoại (transcript rỗng còn tệ hơn
        // transcript dài).
        Assert.Equal(0, BriefContextWindow.ComputeSkip(Turns(1, 500_000), summarizedTurnCount: 1, approvedTurnCount: 1));
    }

    [Fact]
    public void ComputeSkip_ClampsStalePointers()
    {
        // Con trỏ lớn hơn số lượt hiện có (dữ liệu cũ, lượt bị xóa) không được đẩy skip vượt danh sách —
        // và lượt cuối luôn được giữ nguyên văn.
        Assert.Equal(4, BriefContextWindow.ComputeSkip(Turns(5, 100), summarizedTurnCount: 999, approvedTurnCount: 999));
    }
}
