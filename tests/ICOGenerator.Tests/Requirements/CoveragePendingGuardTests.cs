using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bản đồ bao phủ và "Điểm cần làm rõ còn tồn đọng" do HAI lời gọi LLM khác nhau chắt ra từ cùng một hội
// thoại, và chúng không bao giờ nhìn thấy nhau — nên chúng nói ngược nhau mà không tầng nào biết.
//
// Ca thật (dự án Learning and Development 7, 42 lượt): bản đồ ghi «Luồng ngoại lệ & trường hợp đặc biệt»,
// «Vòng đời & trạng thái» và «Dữ liệu / danh mục chính» đều [RÕ], trong khi cùng lúc đó hệ thống đang giữ
// bảy điểm tồn đọng thuộc đúng ba nhóm ấy:
//
//   - "Chưa rõ nhân viên có đăng ký lại được sau khi ticket bị Reject hay không"      → Luồng ngoại lệ
//   - "Chưa rõ kết quả Complete/Not Complete/No Show dùng để xử lý bước nào tiếp theo" → Vòng đời & trạng thái
//   - "Chưa rõ xử lý khi Item ID và Item Title không tạo thành cặp mã–tên duy nhất"    → Dữ liệu / danh mục
//
// [RÕ] không phải một nhãn trạng thái mà là một LỆNH CẤM: requirement-chat.v4.md cấm BA hỏi lại nhóm đã
// [RÕ]. Nên bảy điểm đó vĩnh viễn không bao giờ được lấy, và bước soạn tài liệu — vốn bị cấm giả định —
// nhận một khoảng trống mà không cổng nào báo.
public class CoveragePendingGuardTests
{
    // Bản đồ được lưu dạng JSON nên các test dưới soi TRƯỜNG đã parse, không soi chuỗi: trạng thái và mẩu
    // còn phải hỏi là thứ những tầng sau đọc, còn cách xếp chữ thì không tầng nào phụ thuộc vào.
    private static ICOGenerator.Contracts.Requirements.CoverageMapItem Row(string? map, string labelPrefix) =>
        CoverageMapParser.Parse(map).First(x => x.Label.StartsWith(labelPrefix, StringComparison.Ordinal));

    [Fact]
    public void ClearRow_IsDowngraded_WhenItsGroupStillHasAPendingItem()
    {
        var map = CoveragePendingGuard.Apply(
            CoverageMapFixture.Map("""
            - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch lớp học cả năm. {nguồn: "lên kế hoạch các lớp học"}
            - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Lớp đầy thì ticket sang Waitlist. {nguồn: "Tiếp tục giữ Waitlist"}
            """),
            new[] { "[Luồng ngoại lệ & trường hợp đặc biệt] Chưa rõ nhân viên có đăng ký lại được sau khi ticket bị Reject hay không" });

        Assert.NotNull(map);
        // Dòng có điểm tồn đọng bị hạ, và mục tồn đọng thành ĐÚNG trường Gap — chỗ cổng readiness đọc.
        var exception = Row(map, "Luồng ngoại lệ");
        Assert.Equal("MỘT PHẦN", exception.Status);
        Assert.Equal("Chưa rõ nhân viên có đăng ký lại được sau khi ticket bị Reject hay không", exception.Gap);
        // …còn dòng không liên quan thì không bị đụng tới.
        Assert.Equal("RÕ", Row(map, "Mục tiêu").Status);
    }

    // Phần "còn thiếu:" không phải một ghi chú nội bộ: RequirementReadinessGate lấy NGUYÊN nó làm câu hỏi
    // hiển thị khi cổng chặn. Nhờ vậy điểm tồn đọng thật sự trở thành câu chặn của cổng, thay vì cổng chặn
    // bằng một dòng khác còn điểm này thì nằm im.
    [Fact]
    public void TheDowngradedRow_BecomesTheQuestionTheGateAsks()
    {
        var map = CoveragePendingGuard.Apply(
            CoverageMapFixture.Map("- Vòng đời & trạng thái: [RÕ] Ticket đi Pending → Enroll/Waitlist → Complete. {nguồn: bảng luồng đã chốt}"),
            new[] { "[Vòng đời & trạng thái] Chưa rõ kết quả Complete/Not Complete/No Show được dùng để xử lý bước nào tiếp theo" });

        var readiness = RequirementReadinessGate.Evaluate(map);

        Assert.False(readiness.Ready);
        Assert.Contains("kết quả Complete/Not Complete/No Show được dùng để xử lý bước nào tiếp theo",
            readiness.Message, StringComparison.Ordinal);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
    }

    // Khối {nguồn: …} phải sống sót và phải ở lại CUỐI dòng — CoverageMapParser.SplitEvidence chỉ nhận nó ở
    // đó. Ghép mẩu "còn thiếu" ra sau khối bằng chứng là làm panel tiến độ mất phần trích dẫn của dòng, tức
    // người dùng mất cách duy nhất để kiểm chứng một dòng bản đồ.
    [Fact]
    public void Downgrade_KeepsTheEvidenceBlockAtTheEnd()
    {
        var map = CoveragePendingGuard.Apply(
            CoverageMapFixture.Map("- Dữ liệu / danh mục chính: [RÕ] Dùng 6 cột Master List đã chốt. {nguồn: bảng cột người dùng đã chốt}"),
            new[] { "[Dữ liệu / danh mục chính] Chưa rõ xử lý khi Item ID và Item Title không tạo thành cặp duy nhất" });

        var item = Assert.Single(CoverageMapParser.Parse(map));

        Assert.Equal("MỘT PHẦN", item.Status);
        Assert.Equal("bảng cột người dùng đã chốt", item.Evidence);
        Assert.Contains("Dùng 6 cột Master List đã chốt", item.Known, StringComparison.Ordinal);
        Assert.StartsWith("Chưa rõ xử lý khi Item ID", item.Gap, StringComparison.Ordinal);
    }

    // Lượt chắt lọc viết "Luồng ngoại lệ" còn bản đồ ghi "Luồng ngoại lệ & trường hợp đặc biệt" — vẫn là
    // một nhóm. So khớp nguyên văn ở đây là để guard câm trong im lặng, cùng lý do mà InterviewTableGate
    // và PermissionMatrixGate đều so bằng tiền tố.
    [Theory]
    [InlineData("Luồng ngoại lệ")]
    [InlineData("Luồng ngoại lệ & trường hợp đặc biệt")]
    public void GroupTag_MatchesTheMapLabelByPrefix_InBothDirections(string tag)
    {
        var map = CoveragePendingGuard.Apply(
            CoverageMapFixture.Map("- Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Lớp đầy thì Waitlist."),
            new[] { $"[{tag}] Chưa rõ ticket Waitlist còn treo khi lớp đã kết thúc" });

        Assert.Equal("MỘT PHẦN", Row(map, "Luồng ngoại lệ").Status);
    }

    // Guard chạy MỘT CHIỀU. Hạ nhầm thì BA hỏi thêm một câu; nâng nhầm thì sinh ra một khoảng trống mà mọi
    // tầng sau tin là đã đủ — hai cái giá không cùng hạng, nên nó không bao giờ được nâng cấp hộ distiller.
    [Fact]
    public void Guard_NeverUpgrades_AndNeverTouchesOtherStatuses()
    {
        var map = CoverageMapFixture.Map("""
            - Thông báo / nhắc nhở: [CHƯA HỎI]
            - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] Người dùng nói không cần báo cáo.
            - Quy mô sử dụng: [MỘT PHẦN] Toàn nhà máy. còn thiếu: bao nhiêu lớp mỗi năm.
            """);

        var guarded = CoveragePendingGuard.Apply(map, new[]
        {
            "[Thông báo / nhắc nhở] Chưa rõ ai nhận email khi ticket chờ duyệt",
            "[Báo cáo / thống kê] Chưa rõ cấp quản lý cần xem báo cáo nào",
            "[Quy mô sử dụng] Chưa rõ mỗi năm mở bao nhiêu lớp"
        });

        Assert.Equal(map, guarded);
    }

    // Thẻ model tự nghĩ ra (không khớp nhãn nào) và mục không gắn thẻ đều bị BỎ QUA: guard fail-open, nó
    // không được phép hạ nhầm một dòng vì một cái thẻ vô nghĩa.
    [Theory]
    [InlineData("[Tích hợp hệ thống ngoài] Chưa rõ nối với SAP kiểu gì")]
    [InlineData("[—] Chưa rõ một điểm không thuộc nhóm nào")]
    [InlineData("Chưa rõ điểm này thuộc nhóm nào — mục không gắn thẻ")]
    public void UnknownOrMissingTag_LeavesTheMapAlone(string pendingItem)
    {
        var map = CoverageMapFixture.Map("- Vòng đời & trạng thái: [RÕ] Ticket đi Pending → Enroll → Complete.");

        Assert.Equal(map, CoveragePendingGuard.Apply(map, new[] { pendingItem }));
    }

    // Thẻ nhóm được gắn cho GUARD đối chiếu, không phải cho BA đọc ra. Nhãn nhóm là từ vựng nội bộ của bản
    // đồ; requirement-chat.v4.md cấm ném nó vào mặt người dùng nghiệp vụ, và
    // CoverageDeadQuestionLoopTests đã phải dựng lưới một lần cho đúng lỗi đó.
    [Fact]
    public void StripGroupTag_RemovesTheInternalLabelBeforeItReachesTheBA()
    {
        Assert.Equal("Chưa rõ ai nhận email khi ticket chờ duyệt",
            CoveragePendingGuard.StripGroupTag("[Thông báo / nhắc nhở] Chưa rõ ai nhận email khi ticket chờ duyệt"));

        // Mục chưa gắn thẻ (bản chắt lọc cũ, hoặc model bỏ quên) đi qua nguyên vẹn — không được nuốt mất.
        Assert.Equal("Chưa rõ ai nhận email", CoveragePendingGuard.StripGroupTag("Chưa rõ ai nhận email"));
    }

    // Nhiều mục cùng một nhóm ⇒ chỉ mục ĐẦU TIÊN thành câu chặn. BA hỏi 1–2 câu mỗi lượt, nên dội cả cụm
    // vào một dòng chỉ làm câu hỏi của cổng thành một danh sách không trả lời được; các mục còn lại vẫn
    // nằm nguyên trong khối "Điểm cần làm rõ còn tồn đọng" của ngữ cảnh chat.
    [Fact]
    public void OnlyTheFirstPendingItemOfAGroup_BecomesTheGap()
    {
        var map = CoveragePendingGuard.Apply(
            CoverageMapFixture.Map("- Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Lớp đầy thì Waitlist."),
            new[]
            {
                "[Luồng ngoại lệ & trường hợp đặc biệt] Chưa rõ đăng ký lại sau khi bị Reject",
                "[Luồng ngoại lệ & trường hợp đặc biệt] Chưa rõ đăng ký trùng lịch"
            });

        Assert.Equal("Chưa rõ đăng ký lại sau khi bị Reject", Row(map, "Luồng ngoại lệ").Gap);
        Assert.DoesNotContain("trùng lịch", map, StringComparison.Ordinal);
    }

    // DÒNG VỪA ĐỔI trong chính lượt distill này thì mục tồn đọng của nó đã cũ hơn dòng — danh sách tồn
    // đọng chắt ở hậu kỳ nên nó chưa từng thấy lượt user vừa rồi.
    //
    // Ca thật (dự án JD Libary 5, lượt 3→4): người dùng kể xong quy trình Excel hiện tại ở lượt 3; lượt 4
    // nhận lại đúng mục tồn đọng chắt từ lượt 2 làm câu chặn và họ dán lại nguyên văn câu vừa gõ.
    [Fact]
    public void ARowThatJustChanged_DoesNotGetTheStalePendingGap()
    {
        var before =
            CoverageMapFixture.Map("- Quy trình hiện tại & điểm khó: [CHƯA HỎI]");
        var after =
            CoverageMapFixture.Map("- Quy trình hiện tại & điểm khó: [RÕ] Hiện dùng 2 file Excel, HRBP tự thêm/sửa/xóa cả hai. "
            + "{nguồn: \"1 file excel danh sách JD\"}");

        var map = CoveragePendingGuard.Apply(
            after,
            new[] { "[Quy trình hiện tại & điểm khó] Chưa rõ quy trình hiện tại tạo và gán JD diễn ra thế nào (các bước, vai trò tham gia)" },
            before);

        Assert.Equal(after, map);
    }

    // Ranh giới: dòng KHÔNG đổi thì mục tồn đọng vẫn còn nguyên giá trị và guard vẫn hạ như cũ. Bản đồ cũ
    // được chép lại từng chữ khi lượt distill không có gì mới cho dòng đó, nên phép so nội dung đủ chặt.
    [Fact]
    public void AnUnchangedRow_StillGetsTheGap()
    {
        var row =
            CoverageMapFixture.Map("- Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Lớp đầy thì ticket sang Waitlist. {nguồn: \"giữ Waitlist\"}");

        var map = CoveragePendingGuard.Apply(
            row,
            new[] { "[Luồng ngoại lệ & trường hợp đặc biệt] Chưa rõ đăng ký lại sau khi bị Reject" },
            row);

        var item = Assert.Single(CoverageMapParser.Parse(map));
        Assert.Equal("MỘT PHẦN", item.Status);
        Assert.Equal("Chưa rõ đăng ký lại sau khi bị Reject", item.Gap);
    }

    [Fact]
    public void NoPendingItems_LeavesTheMapUntouched()
    {
        var map = CoverageMapFixture.Map("- ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch lớp học.");

        Assert.Equal(map, CoveragePendingGuard.Apply(map, Array.Empty<string>()));
        Assert.Null(CoveragePendingGuard.Apply(null, new[] { "[Vòng đời & trạng thái] Chưa rõ gì đó" }));
    }
}
