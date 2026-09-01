using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Vòng lặp câu hỏi chết thứ hai — ca thật, dự án "JD Libary 7", ba lượt cuối của một buổi phỏng vấn 102
// lượt:
//
//   Lượt 100 — BA:          bày bảng thông báo, cả 5 sự kiện đều "To: *chưa chọn*".
//   Lượt 101 — Người dùng:  gửi bảng với đủ To/CC cho 4 sự kiện và bỏ tích sự kiện thứ 5.
//   Lượt 102 — BA:          "Chưa rõ người nhận cho từng sự kiện thông báo — anh/chị cho mình xin thông
//                            tin này nhé?"
//
// Bảng ĐÃ lưu (Project.NotificationMap), khối "đã chốt" ĐÃ được đính vào lượt distill, và "Điểm cần làm rõ
// còn tồn đọng" thì rỗng. Chỗ hỏng nằm đúng một dòng của bản đồ bao phủ, và nó tự mâu thuẫn:
//
//   - Thông báo / nhắc nhở: [MỘT PHẦN] Email theo 4 sự kiện …; đã chốt To/CC riêng từng sự kiện, không gửi
//     khi JD Được tạo. còn thiếu: Chưa rõ người nhận cho từng sự kiện thông báo {nguồn: bảng thông báo …}
//
// Distiller cập nhật phần tóm tắt theo bảng mới nhưng giữ nguyên mẩu "còn thiếu" của bản đồ cũ — mà
// RequirementReadinessGate lấy NGUYÊN mẩu đó làm câu chặn. Không lối thoát: NotificationMapGate không bao
// giờ bày lại bảng đã chốt, và khối "đã chốt" cấm BA hỏi lẻ nhóm này. Người dùng trả lời bao nhiêu lần
// cũng nhận lại đúng câu ấy, nút "Write Requirement" khóa vĩnh viễn.
public class CoverageConfirmedTableGuardTests
{
    // Bản đồ lưu dạng JSON ⇒ các test soi TRƯỜNG đã parse thay vì chuỗi: trạng thái, phần đã ghi nhận,
    // mẩu còn phải hỏi và bằng chứng là thứ những tầng sau đọc; cách xếp chữ thì không tầng nào dựa vào.
    private static ICOGenerator.Contracts.Requirements.CoverageMapItem Row(string? map, string labelPrefix) =>
        CoverageMapParser.Parse(map).First(x => x.Label.StartsWith(labelPrefix, StringComparison.Ordinal));

    private const string ConfirmedNotifications = """
        [
          { "entity": "JD", "event": "Chờ HRBP duyệt", "needed": true, "to": ["HRBP"], "cc": ["Manager của orgUnit"] },
          { "entity": "JD", "event": "Chờ HoD duyệt", "needed": true, "to": ["HoD của department"], "cc": ["Manager của orgUnit"] },
          { "entity": "JD", "event": "Available", "needed": true, "to": ["Manager của orgUnit"] },
          { "entity": "JD", "event": "Bị từ chối", "needed": true, "to": ["Manager của orgUnit"] },
          { "entity": "JD", "event": "Được tạo", "needed": false, "to": [] }
        ]
        """;

    private const string ConfirmedMatrix = """
        [
          { "screen": "JD Library", "function": "Xem danh sách JD",
            "grants": [ { "role": "HRBP", "scope": "tất cả" }, { "role": "Nhân viên", "scope": "của mình" } ] },
          { "screen": "JD Creation", "function": "Tạo JD",
            "grants": [ { "role": "Manager của orgUnit", "scope": "của mình" } ] }
        ]
        """;

    [Fact]
    public void NotificationRow_IsRaised_AndTheStaleGapIsDropped_WhenTheTableIsConfirmed()
    {
        var map = CoverageConfirmedTableGuard.Apply(
            CoverageMapFixture.Map("""
            - ★ Mục tiêu / bài toán: [RÕ] Quản lý và phê duyệt JD. {nguồn: "Đồng ý"}
            - Thông báo / nhắc nhở: [MỘT PHẦN] Email theo 4 sự kiện; đã chốt To/CC riêng từng sự kiện. còn thiếu: Chưa rõ người nhận cho từng sự kiện thông báo {nguồn: bảng thông báo người dùng đã chốt}
            """),
            permissionMatrixJson: null,
            ConfirmedNotifications);

        Assert.NotNull(map);
        var notification = Row(map, "Thông báo");
        Assert.Equal("RÕ", notification.Status);
        // Mẩu còn phải hỏi là thứ cổng đem ra hỏi ⇒ phải biến mất hẳn, không chỉ đổi trạng thái dòng.
        Assert.Empty(notification.Gap);
        // Dòng không liên quan giữ nguyên.
        var goal = Row(map, "Mục tiêu");
        Assert.Equal("RÕ", goal.Status);
        Assert.Equal("Quản lý và phê duyệt JD.", goal.Known);
    }

    // Triệu chứng người dùng thật sự nhìn thấy: cổng thôi chặn và nút "Write Requirement" mở.
    [Fact]
    public void TheRepairedMap_UnlocksTheGate_InsteadOfAskingTheSameQuestionAgain()
    {
        var stuck = CoverageMapFixture.Map("""
            - ★ Mục tiêu / bài toán: [RÕ] Quản lý và phê duyệt JD. {nguồn: "Đồng ý"}
            - Phân quyền theo nghiệp vụ: [RÕ] Đã chốt quyền từng vai. {nguồn: bảng phân quyền người dùng đã chốt}
            - Thông báo / nhắc nhở: [MỘT PHẦN] Email theo 4 sự kiện. còn thiếu: Chưa rõ người nhận cho từng sự kiện thông báo
            """);

        // Trước khi sửa: cổng chặn bằng ĐÚNG câu BA đã hỏi ở lượt 102.
        var before = RequirementReadinessGate.Evaluate(stuck);
        Assert.False(before.Ready);
        Assert.Contains("Chưa rõ người nhận cho từng sự kiện thông báo", before.Message, StringComparison.Ordinal);

        var after = RequirementReadinessGate.Evaluate(
            CoverageConfirmedTableGuard.Apply(stuck, permissionMatrixJson: null, ConfirmedNotifications));

        Assert.True(after.Ready);
    }

    // Số đếm của dòng lấy từ chính bảng vừa lưu — 4 sự kiện gửi, 1 sự kiện người dùng tắt — nên không câu
    // chữ nào của model chen được vào đây một điều bảng không chứa.
    [Fact]
    public void TheRewrittenSummary_CountsTheRowsOfTheConfirmedTable()
    {
        var map = CoverageConfirmedTableGuard.Apply(
            CoverageMapFixture.Map("- Thông báo / nhắc nhở: [CHƯA HỎI]"),
            permissionMatrixJson: null,
            ConfirmedNotifications);

        Assert.NotNull(map);
        var row = Row(map, "Thông báo");
        Assert.Contains("4 sự kiện gửi email kèm người nhận riêng", row.Known, StringComparison.Ordinal);
        Assert.Contains("1 sự kiện người dùng chọn không gửi", row.Known, StringComparison.Ordinal);
        Assert.Equal("bảng thông báo người dùng đã chốt", row.Evidence);
    }

    // Dòng phân quyền đi theo ĐÚNG luật đó: cùng hình dạng "chốt bằng bảng, cấm hỏi lại", nên cùng một mẩu
    // "còn thiếu" sót lại cũng khóa cổng y hệt.
    [Fact]
    public void PermissionRow_FollowsTheSameRule()
    {
        var map = CoverageConfirmedTableGuard.Apply(
            CoverageMapFixture.Map("- Phân quyền theo nghiệp vụ: [MỘT PHẦN] Đã chốt quyền từng vai. còn thiếu: bảng phân quyền theo màn hình chưa được chốt"),
            ConfirmedMatrix,
            notificationMapJson: null);

        Assert.NotNull(map);
        var row = Row(map, "Phân quyền");
        Assert.Equal("RÕ", row.Status);
        Assert.Contains("2 chức năng trên 2 màn hình, 3 vai trò", row.Known, StringComparison.Ordinal);
        Assert.Empty(row.Gap);
    }

    // Chưa có bảng ⇒ guard phải IM. Luật một chiều của hai nhóm này là "chưa có bảng thì KHÔNG BAO GIỜ
    // [RÕ]"; nâng dòng ở đây là dựng lại đúng ca mà cả hai cái bảng sinh ra để chặn.
    [Fact]
    public void NothingIsRaised_WhenTheTableWasNeverConfirmed()
    {
        var map = CoverageMapFixture.Map("- Thông báo / nhắc nhở: [MỘT PHẦN] Có gửi email khi đổi trạng thái. còn thiếu: ai nhận email của từng sự kiện");

        Assert.Equal(map, CoverageConfirmedTableGuard.Apply(map, permissionMatrixJson: null, notificationMapJson: null));
        Assert.Equal(map, CoverageConfirmedTableGuard.Apply(map, permissionMatrixJson: "[]", notificationMapJson: "[]"));
    }

    // Bảng ghi TRƯỚC khi bất biến "tích Cần thì phải có người nhận" tồn tại: còn dòng cần gửi mà chưa có
    // ai. Đó là thiếu THẬT — "cần báo nhưng chưa chốt được ai" — nên guard không được đóng dấu [RÕ] lên nó.
    [Fact]
    public void NothingIsRaised_WhenAnOldTableStillHasARowNeedingRecipients()
    {
        const string partial = """
            [
              { "entity": "JD", "event": "Chờ HRBP duyệt", "needed": true, "to": ["HRBP"] },
              { "entity": "JD", "event": "Available", "needed": true, "to": [] }
            ]
            """;
        var map = CoverageMapFixture.Map("- Thông báo / nhắc nhở: [MỘT PHẦN] Mới chốt một phần. còn thiếu: người nhận của sự kiện Available");

        Assert.Equal(map, CoverageConfirmedTableGuard.Apply(map, permissionMatrixJson: null, partial));
    }

    // Người dùng vừa nói BA hiểu SAI nhóm này (AskedQuestionHistory.ReopenNote) — đó là lần duy nhất đường
    // hỏi lại được mở ra, và nó do chính họ mở. Guard đè lên là lấy mất đường ấy.
    [Fact]
    public void AReopenedRow_IsLeftAlone_BecauseTheUserJustOpenedItBackUp()
    {
        var map = CoverageMapFixture.Map(
            "- Thông báo / nhắc nhở: [MỘT PHẦN] Email theo 4 sự kiện. còn thiếu: "
            + AskedQuestionHistory.ReopenNote + " — cần hỏi lại người nhận của sự kiện Bị từ chối");

        Assert.Equal(map, CoverageConfirmedTableGuard.Apply(map, permissionMatrixJson: null, ConfirmedNotifications));
    }

    // [KHÔNG ÁP DỤNG] không chặn cổng, và nó là một quyết định đã ghi nhận ⇒ không có gì để "sửa".
    [Fact]
    public void ANotApplicableRow_IsLeftAlone()
    {
        var map = CoverageMapFixture.Map("- Thông báo / nhắc nhở: [KHÔNG ÁP DỤNG] Ứng dụng một người dùng, không báo cho ai.");

        Assert.Equal(map, CoverageConfirmedTableGuard.Apply(map, permissionMatrixJson: null, ConfirmedNotifications));
    }

    // Nhãn nhóm do một lượt distill viết chệch phần đuôi vẫn phải khớp — cùng phép so tiền tố hai chiều với
    // CoveragePendingGuard.FindGap. Không có nó thì guard câm trong im lặng, đúng cái kiểu hỏng khó thấy
    // nhất.
    [Fact]
    public void TheGroupLabel_MatchesByPrefix_SoADriftedLabelStillGetsRepaired()
    {
        var map = CoverageConfirmedTableGuard.Apply(
            CoverageMapFixture.Map("- ★ Thông báo: [MỘT PHẦN] Email theo sự kiện. còn thiếu: người nhận từng sự kiện"),
            permissionMatrixJson: null,
            ConfirmedNotifications);

        Assert.NotNull(map);
        var row = Row(map, "Thông báo");
        Assert.Equal("RÕ", row.Status);
        Assert.True(row.IsCore);
    }
}
