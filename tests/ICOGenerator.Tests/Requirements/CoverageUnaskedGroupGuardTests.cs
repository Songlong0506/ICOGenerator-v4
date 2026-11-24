using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Vòng lặp câu hỏi chết — ca thật, dự án "JD Libary 7", ba lượt cuối của một buổi phỏng vấn 102 lượt: dòng
// «Thông báo / nhắc nhở» của bản đồ tự mâu thuẫn
//
//   - Thông báo / nhắc nhở: [MỘT PHẦN] Email theo 4 sự kiện …; đã chốt To/CC riêng từng sự kiện …
//     còn thiếu: Chưa rõ người nhận cho từng sự kiện thông báo
//
// Distiller cập nhật phần tóm tắt nhưng giữ nguyên câu hỏi của lượt cũ — mà RequirementReadinessGate lấy
// NGUYÊN câu đó làm câu chặn, trong khi BA bị cấm hỏi lẻ nhóm này. Nút "Write Requirement" khóa vĩnh viễn.
//
// Nay nhóm thông báo là nhóm KHÔNG-HỎI (CoverageGroupKey.Unasked): không chốt ở buổi phỏng vấn (sản phẩm của
// buổi là một POC không gửi email nào). Dòng LUÔN ra khỏi guard ở trạng thái không chặn, giữ điều người dùng
// tự kể, và mọi câu hỏi của nhóm bị xoá.
public class CoverageUnaskedGroupGuardTests
{
    private static (List<CoverageMapItem> Items, List<OpenQuestionEntry> Questions) Apply(string bullets)
    {
        var items = CoverageMapFixture.Items(bullets).ToList();
        var questions = CoverageMapFixture.Questions(bullets);
        CoverageUnaskedGroupGuard.Apply(items, questions);
        return (items, questions);
    }

    private static CoverageMapItem Row(IEnumerable<CoverageMapItem> items, string labelPrefix) =>
        items.First(x => x.Label.StartsWith(labelPrefix, StringComparison.Ordinal));

    // Triệu chứng người dùng thật sự nhìn thấy: cổng thôi chặn và nút "Write Requirement" mở.
    [Fact]
    public void TheRepairedMap_UnlocksTheGate_InsteadOfAskingTheSameQuestionAgain()
    {
        const string stuck = """
            - ★ Mục tiêu / bài toán: [RÕ] Quản lý và phê duyệt JD.
            - Thông báo / nhắc nhở: [MỘT PHẦN] Email theo 4 sự kiện. còn thiếu: Chưa rõ người nhận cho từng sự kiện thông báo
            """;

        // Trước khi sửa: cổng vẫn CHẶN — dòng thông báo còn [MỘT PHẦN]. Nhưng nó không phát lại câu BA đã hỏi:
        // CoverageQuestionGuard coi mọi câu hỏi của nhóm không-hỏi là câu hỏi CHẾT và cổng rơi về nhánh phát
        // lại. Guard này là thứ duy nhất MỞ được cổng — xoá câu hỏi không nâng nổi trạng thái.
        var before = RequirementReadinessGate.Evaluate(CoverageMapFixture.Map(stuck), CoverageMapFixture.Questions(stuck));
        Assert.False(before.Ready);
        Assert.DoesNotContain("Chưa rõ người nhận cho từng sự kiện thông báo", before.Message, StringComparison.Ordinal);
        Assert.Contains("Email theo 4 sự kiện", before.Message, StringComparison.Ordinal);

        var (items, questions) = Apply(stuck);
        var after = RequirementReadinessGate.Evaluate(CoverageMapParser.Serialize(items), questions);

        Assert.True(after.Ready);
    }

    // `known` chỉ chở lời NGƯỜI DÙNG: guard giữ nguyên điều họ tự kể và không ghi thêm chữ nào của hệ thống.
    // Lời giải thích "người nhận chưa chốt" là chữ của giao diện (CoverageMapItem.Note), không phải dữ liệu.
    [Fact]
    public void Notifications_AreSettled_KeepingExactlyWhatTheUserSaid()
    {
        var (items, questions) = Apply(
            "- Thông báo / nhắc nhở: [MỘT PHẦN] Duyệt xong thì báo cho người tạo. còn thiếu: ai nhận email của từng sự kiện");

        var row = Assert.Single(items);
        Assert.Equal(CoverageStatus.Clear, row.Status);
        Assert.Equal(new[] { "Duyệt xong thì báo cho người tạo." }, row.Known);
        Assert.Empty(questions);
    }

    // Người dùng không nói gì về thông báo cả ⇒ dòng vẫn không chặn, và `known` vẫn rỗng.
    [Fact]
    public void AnUnaskedNotificationRow_NoLongerBlocksTheGate()
    {
        const string bullets = """
            - ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép.
            - Thông báo / nhắc nhở: [CHƯA HỎI]
            """;

        var before = RequirementReadinessGate.Evaluate(CoverageMapFixture.Map(bullets), CoverageMapFixture.Questions(bullets));
        Assert.False(before.Ready);

        var (items, questions) = Apply(bullets);

        Assert.Empty(Row(items, "Thông báo").Known);
        Assert.True(RequirementReadinessGate.Evaluate(CoverageMapParser.Serialize(items), questions).Ready);
    }

    // Guard chạy ở MỌI lượt distill trên chính bản đồ nó vừa ghi — phải là phép bất động.
    [Fact]
    public void RepeatedPasses_ChangeNothing()
    {
        var (first, _) = Apply("- Thông báo / nhắc nhở: [MỘT PHẦN] Duyệt xong thì báo cho người tạo.");
        var once = CoverageMapParser.Serialize(first);

        var second = CoverageMapParser.Parse(once).ToList();
        CoverageUnaskedGroupGuard.Apply(second, new List<OpenQuestionEntry>());

        Assert.Equal(once, CoverageMapParser.Serialize(second));
    }

    // Lời đính chính của người dùng về thông báo KHÔNG mở lại đường hỏi: không có đường hỏi nào cho nhóm
    // này. Giữ câu hỏi lại là khóa cổng bằng một câu BA bị cấm hỏi.
    [Fact]
    public void AReopenedNotificationRow_IsStillSettled()
    {
        var (items, questions) = Apply(
            "- Thông báo / nhắc nhở: [MỘT PHẦN] Email theo 4 sự kiện. còn thiếu: "
            + AskedQuestionHistory.ReopenNote + " — cần hỏi lại người nhận của sự kiện Bị từ chối");

        Assert.Equal(CoverageStatus.Clear, Assert.Single(items).Status);
        Assert.Empty(questions);
    }

    // [KHÔNG ÁP DỤNG] không chặn cổng, và nó là một quyết định đã ghi nhận ⇒ không có gì để "sửa".
    [Fact]
    public void ANotApplicableRow_IsLeftAlone()
    {
        var (items, _) = Apply("- Thông báo / nhắc nhở: [KHÔNG ÁP DỤNG] Ứng dụng một người dùng, không báo cho ai.");

        Assert.Equal(CoverageStatus.NotApplicable, Assert.Single(items).Status);
    }

    // Nhóm KHÁC không bị guard đụng tới — kể cả khi nó đang [MỘT PHẦN] kèm câu hỏi.
    [Fact]
    public void AskedGroups_AreUntouched()
    {
        var (items, questions) = Apply(
            "- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Đơn quá 3 ngày cần HoD duyệt. còn thiếu: đơn bị từ chối thì sao");

        Assert.Equal(CoverageStatus.Partial, Assert.Single(items).Status);
        Assert.Single(questions);
    }

    // Nhãn nhóm do một lượt distill viết chệch phần đuôi vẫn phải khớp — cùng phép so tiền tố hai chiều
    // dùng chung ở CoverageMapParser.IsSameGroup. Không có nó thì guard câm trong im lặng, đúng cái kiểu
    // hỏng khó thấy nhất.
    [Fact]
    public void TheGroupLabel_MatchesByPrefix_SoADriftedLabelStillGetsRepaired()
    {
        var (items, questions) = Apply("- ★ Thông báo: [MỘT PHẦN] Email theo sự kiện. còn thiếu: người nhận từng sự kiện");

        var row = Row(items, "Thông báo");
        Assert.Equal(CoverageStatus.Clear, row.Status);
        Assert.True(row.IsCore);
        Assert.Empty(questions);
    }

    // Chú thích của hệ thống trên panel: chỉ ở nhóm không-hỏi, và không ở dòng [KHÔNG ÁP DỤNG] (không có
    // người nhận nào để mà "chưa chốt").
    [Fact]
    public void TheUnaskedNote_IsAPresentationConcern_OnlyOnUnaskedRowsThatApply()
    {
        var items = CoverageMapFixture.Items("""
            - Thông báo / nhắc nhở: [RÕ] Duyệt xong thì báo cho người tạo.
            - Quy tắc nghiệp vụ & ràng buộc: [RÕ] Đơn quá 3 ngày cần HoD duyệt.
            """).ToList();

        Assert.Contains("chưa chốt", Row(items, "Thông báo").Note, StringComparison.Ordinal);
        Assert.Null(Row(items, "Quy tắc").Note);
        Assert.DoesNotContain(Row(items, "Thông báo").Note!, CoverageMapParser.Serialize(items), StringComparison.Ordinal);

        var na = CoverageMapFixture.Items("- Thông báo / nhắc nhở: [KHÔNG ÁP DỤNG] Không báo cho ai.").Single();
        Assert.Null(na.Note);
    }
}
