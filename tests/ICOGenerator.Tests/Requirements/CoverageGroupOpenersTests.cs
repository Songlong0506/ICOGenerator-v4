using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Câu MỞ ĐẦU của từng nhóm — thứ RequirementReadinessGate dùng khi nó phải tự đặt câu hỏi thay BA và bản
// đồ không cho nó mẩu "còn thiếu:" nào để bám.
//
// Test này giữ đúng một bất biến: KHÔNG nhóm nào rơi vào một lượt trống nghĩa. Bản trước phát MỘT câu duy
// nhất cho cả 12 nhóm ("Anh/chị kể giúp mình phần này trong công việc thực tế hiện đang diễn ra thế nào?"),
// và ca thật ở dự án JD Library lượt 76 cho thấy giá của nó: người dùng đáp "mình chưa hiểu câu hỏi, hãy
// hỏi rõ hơn" — mất trắng một vòng ở cuối buổi phỏng vấn thứ 78.
public class CoverageGroupOpenersTests
{
    // Prompt là nguồn chân lý của DANH SÁCH nhóm (CoverageChecklist bóc thẳng từ đó). Thêm một nhóm vào
    // prompt mà quên viết câu mở đầu cho nó ⇒ fail ở đây, chứ không phải âm thầm rơi về câu dự phòng cuối
    // trên màn hình người dùng.
    [Fact]
    public void EveryGroupInTheRealPromptHasAnOpener()
    {
        var groups = CoverageChecklist.Parse(CoveragePromptFixture.Read());

        Assert.Equal(12, groups.Count);
        Assert.All(groups, g => Assert.False(
            string.IsNullOrWhiteSpace(CoverageGroupOpeners.Find(g.Label)),
            $"Nhóm «{g.Label}» chưa có câu mở đầu trong CoverageGroupOpeners."));
    }

    // Đây là lượt duy nhất người dùng nhìn thấy khi cổng chặn, và nó không có chip nào ⇒ phải là một CÂU
    // HỎI thật, đủ nghĩa một mình.
    [Fact]
    public void EveryOpenerIsAQuestion()
    {
        foreach (var group in CoverageChecklist.Parse(CoveragePromptFixture.Read()))
        {
            var opener = CoverageGroupOpeners.Find(group.Label)!;
            Assert.EndsWith("?", opener.Trim(), StringComparison.Ordinal);
            // "phần này", "như đã nêu"… trỏ tới một chỗ chỉ hệ thống đang nhìn thấy: người dùng chỉ có ô
            // chat cuối trên màn hình. Đúng cụm đã làm chết lượt 76.
            Assert.DoesNotContain("phần này", opener, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Từ vựng NỘI BỘ của bản đồ không được nằm trong câu hỏi. Nhãn nhóm vẫn được cổng đặt trong ngoặc ở câu
    // dẫn (người dùng thấy đúng các nhãn đó ở panel "Tiến độ khai thác"), nhưng câu hỏi thì phải bằng ngôn
    // ngữ công việc của họ.
    [Theory]
    [InlineData("vòng đời")]
    [InlineData("đặc tả")]
    [InlineData("phạm vi")]
    [InlineData("bản đồ bao phủ")]
    [InlineData("[MỘT PHẦN]")]
    public void NoOpenerLeaksInternalVocabulary(string jargon)
    {
        foreach (var group in CoverageChecklist.Parse(CoveragePromptFixture.Read()))
            Assert.DoesNotContain(jargon, CoverageGroupOpeners.Find(group.Label)!, StringComparison.OrdinalIgnoreCase);
    }

    // Hai nhóm bị prompt CẤM hỏi bằng câu hỏi (chốt bằng BẢNG ở cuối buổi). Phát cho chúng một câu khai
    // thác là cổng tự phá chốt chặn mà hai cái bảng sinh ra để dựng — người dùng bấm mấy chip vai trò và
    // tài liệu đóng băng thành "mọi thay đổi trạng thái gửi cho cả bốn nhóm". Câu của chúng chỉ MỜI người
    // dùng nhắn một tiếng để phần đó được đưa ra rà.
    [Theory]
    [InlineData("Thông báo / nhắc nhở")]
    [InlineData("Phân quyền theo nghiệp vụ")]
    public void TableBackedGroupsInviteAReviewInsteadOfMiningForAnswers(string label)
    {
        var opener = CoverageGroupOpeners.Find(label)!;

        Assert.Contains("rà cùng", opener, StringComparison.Ordinal);
        // …và KHÔNG hứa hẹn "một cái bảng": dự án không có vòng đời trạng thái nào thì bảng thông báo không
        // bao giờ được bày và nhóm quay về đường hỏi bằng câu hỏi (NotificationMapGate). Cổng chỉ có bản đồ
        // bao phủ trong tay nên nó không phân biệt được hai ca đó.
        Assert.DoesNotContain("bảng", opener, StringComparison.OrdinalIgnoreCase);
    }

    // So khớp hai chiều bằng tiền tố, cùng lý do với CoveragePendingGuard.FindGap: distiller viết nhãn ngắn
    // hơn bản gốc là chuyện thường, và một phép so nguyên văn sẽ làm cả bảng này câm trong im lặng.
    [Fact]
    public void MatchesALabelWrittenShorterThanThePromptOne()
    {
        Assert.Equal(
            CoverageGroupOpeners.Find("Luồng ngoại lệ & trường hợp đặc biệt"),
            CoverageGroupOpeners.Find("Luồng ngoại lệ"));
    }

    // Nhãn model tự nghĩ ra ⇒ null, để cổng dùng câu dẫn chung thay vì hỏi người dùng về một nhóm không có
    // trong checklist nào.
    [Fact]
    public void ReturnsNullForALabelThatIsNotAGroup()
    {
        Assert.Null(CoverageGroupOpeners.Find("Tích hợp hệ thống ngoài"));
        Assert.Null(CoverageGroupOpeners.Find(null));
        Assert.Null(CoverageGroupOpeners.Find("   "));
    }
}
