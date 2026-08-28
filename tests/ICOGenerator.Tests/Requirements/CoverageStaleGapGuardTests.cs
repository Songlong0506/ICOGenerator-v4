using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Một dòng bản đồ không được vừa ghi nhận một điều vừa ghi rằng chính điều đó còn thiếu.
//
// Ca thật (dự án JD Libary 4, buổi 24 lượt) — cả bốn dòng dưới đây lấy nguyên văn từ bản xuất hội thoại:
//
//   - Người dùng trả lời điểm đau ở lượt 5. Distiller ghi trọn bốn điểm đó vào dòng «Quy trình hiện tại &
//     điểm khó» ở [RÕ], nhưng dòng «Mục tiêu / bài toán» vẫn giữ "còn thiếu: Chưa rõ điểm khó chịu nhất…"
//     suốt 19 lượt sau đó.
//   - Dòng «Quy tắc nghiệp vụ & ràng buộc» liệt kê đủ ba quy tắc rồi vẫn kèm "còn thiếu: Chưa rõ các quy
//     tắc bắt buộc … (ví dụ mã JD duy nhất, Responsibility tổng % bằng 100)" — đúng ba quy tắc nó vừa ghi.
//
// Thiệt hại là một VÒNG LẶP KÍN, không phải một dòng xấu: RequirementReadinessGate lấy nguyên mẩu đó làm
// câu chặn, nên lượt 24 của buổi ấy là câu hỏi của lượt 4 phát lại nguyên văn — thứ người dùng đã trả lời
// 19 lượt trước. Distiller lại chép mẩu cũ sang lượt sau, và nút "Write Requirement" khoá vĩnh viễn.
public class CoverageStaleGapGuardTests
{
    // Nguyên văn hai dòng của buổi JD Libary 4.
    private const string MucTieu =
        "- ★ Mục tiêu / bài toán: [MỘT PHẦN] App quản lý danh sách JD trong nhà máy và gán JD cho nhân viên. "
        + "còn thiếu: Chưa rõ điểm khó chịu nhất khi làm việc bằng 2 file Excel là gì (phải sửa tay ở 2 file, "
        + "không biết JD nào đang gán cho ai, người khác muốn xem phải hỏi HRBP, hay file dễ sửa nhầm không "
        + "biết ai sửa) {nguồn: \"đây là app để quản lý danh sách JD ở trong nhà máy\"}";

    private const string QuyTrinhHienTai =
        "- Quy trình hiện tại & điểm khó: [RÕ] Hiện tại dùng 2 file Excel (1 file danh sách JD, 1 file JD gán "
        + "cho nhân viên), HRBP tự thao tác. Điểm khó: sửa tay 2 file, không biết JD nào đang gán cho ai, "
        + "người khác xem phải hỏi HRBP, dễ sửa nhầm không biết ai sửa. {nguồn: \"tất cả các thông tin mà bạn gợi ý ở trên\"}";

    [Fact]
    public void GapAnsweredByAnotherClearRow_IsDropped()
    {
        var map = CoverageStaleGapGuard.Apply(MucTieu + "\n" + QuyTrinhHienTai);

        Assert.NotNull(map);
        Assert.DoesNotContain("còn thiếu:", map, StringComparison.OrdinalIgnoreCase);
        // Trạng thái, tóm tắt và bằng chứng của dòng đều còn nguyên — guard chỉ xoá mẩu đã chết.
        Assert.Contains("Mục tiêu / bài toán: [MỘT PHẦN] App quản lý danh sách JD", map, StringComparison.Ordinal);
        Assert.Contains("{nguồn: \"đây là app để quản lý danh sách JD ở trong nhà máy\"}", map, StringComparison.Ordinal);
        Assert.Contains("★", map, StringComparison.Ordinal);
    }

    [Fact]
    public void GapAnsweredByTheRowItself_IsDropped()
    {
        var map = CoverageStaleGapGuard.Apply(
            "- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Mã JD phải duy nhất; Responsibility phải có tổng % "
            + "bằng 100; tất cả các trường của JD (mã JD, OrgUnit, JobTitle, JobFunction, PC Level, Skill, "
            + "Degree, Major, Responsibility) là bắt buộc, không được để trống. còn thiếu: Chưa rõ các quy tắc "
            + "bắt buộc cho các trường thông tin JD (ví dụ mã JD duy nhất, Responsibility tổng % bằng 100) "
            + "{nguồn: \"mã JD phải duy nhất, Responsibility phải có tổng % bằng 100\"}");

        Assert.NotNull(map);
        Assert.DoesNotContain("còn thiếu:", map, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[MỘT PHẦN] Mã JD phải duy nhất", map, StringComparison.Ordinal);
    }

    // Cắt vòng lặp là đủ; guard KHÔNG được tự kết luận "vậy là đã đủ". Bằng chứng ở đây do LLM chắt, khác
    // CoverageConfirmedTableGuard nơi bằng chứng là từng ô người dùng tự tay bấm. Dòng mất mẩu vẫn
    // [MỘT PHẦN] và cổng rơi về nhánh PHÁT LẠI — một câu hỏi ĐÓNG LẠI ĐƯỢC bằng một lượt, thay cho một câu
    // hỏi mà người dùng đã trả lời rồi.
    [Fact]
    public void TheRowStaysPartial_AndTheGateAsksAQuestionThatCanBeClosed()
    {
        var map = CoverageStaleGapGuard.Apply(MucTieu + "\n" + QuyTrinhHienTai);

        var readiness = RequirementReadinessGate.Evaluate(map);

        Assert.False(readiness.Ready);
        Assert.Contains("Mình đang ghi nhận", readiness.Message, StringComparison.Ordinal);
        Assert.Contains("App quản lý danh sách JD", readiness.Message, StringComparison.Ordinal);
        // …và KHÔNG còn phát lại câu người dùng đã trả lời ở lượt 5.
        Assert.DoesNotContain("điểm khó chịu nhất", readiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Chiều ngược lại quan trọng ngang thế: một mẩu CÒN SỐNG mà bị xoá là mất một câu hỏi thật. Hai mẩu
    // dưới đây cũng của buổi JD Libary 4 và cùng nằm cạnh phần tóm tắt nói về đúng chủ đề đó — nhưng thứ
    // chúng hỏi (ai được XOÁ danh mục JD; JD bị TRÙNG TÊN thì sao) thì phần tóm tắt không trả lời.
    [Theory]
    [InlineData("- Dữ liệu / danh mục chính: [MỘT PHẦN] Có 2 danh sách chính: danh sách JD và danh sách JD gán "
        + "cho nhân viên. Mỗi JD gồm: mã JD, OrgUnit, JobTitle, JobFunction, PC Level, Skill, Degree, Major, "
        + "Responsibility. Manager tự quản lý JD của orgUnit mình. còn thiếu: Chưa rõ ai là người quản lý danh "
        + "mục JD (thêm, sửa, xóa) trong ứng dụng mới")]
    [InlineData("- Luồng ngoại lệ & trường hợp đặc biệt: [MỘT PHẦN] JD đã được HoD approve thì không sửa trực "
        + "tiếp được; muốn chỉnh sửa thì upgrade version và duyệt lại từ đầu. còn thiếu: Chưa rõ có trường hợp "
        + "ngoại lệ nào không, ví dụ JD bị trùng tên hoặc cần chỉnh sửa sau khi đã available")]
    public void AGapTheRowDoesNotActuallyAnswer_IsKept(string line)
    {
        Assert.Contains("còn thiếu:", CoverageStaleGapGuard.Apply(line), StringComparison.OrdinalIgnoreCase);
    }

    // Cụm tín hiệu tái mở là một LỆNH của người dùng ("nhóm này BA hiểu sai, hỏi lại giúp tôi"), không phải
    // một câu hỏi chết. Xoá nó là bịt đúng đường họ vừa mở — và AskedQuestionHistory.ReopenedGroups đọc
    // chính cụm đó để miễn phanh chống hỏi lại cho nhóm ấy.
    [Fact]
    public void TheReopenMarker_IsNeverDropped()
    {
        var line = "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] Manager tạo JD, HRBP duyệt, HoD duyệt cuối. "
            + "còn thiếu: " + AskedQuestionHistory.ReopenNote + " — cần hỏi lại và chốt lại. Manager tạo JD, "
            + "HRBP duyệt, HoD duyệt cuối.";

        Assert.Equal(line, CoverageStaleGapGuard.Apply(line));
    }

    [Fact]
    public void AMapWithNothingToRepair_IsReturnedUnchanged()
    {
        Assert.Equal(QuyTrinhHienTai, CoverageStaleGapGuard.Apply(QuyTrinhHienTai));
        Assert.Null(CoverageStaleGapGuard.Apply(null));
    }

    // Đường vào THỨ HAI của cùng một mẩu chết: danh sách "Điểm cần làm rõ còn tồn đọng" chắt ở hậu kỳ nên
    // nó luôn cũ hơn bản đồ đúng một lượt, và CoveragePendingGuard ghi thẳng mục đầu tiên của mỗi nhóm vào
    // dòng bản đồ. Không lọc ở đây thì mẩu vừa được dọn quay lại ngay ở lượt sau.
    [Fact]
    public void APendingItemTheMapAlreadyAnswers_NeverReachesTheMap()
    {
        var pending = new[]
        {
            "[Mục tiêu / bài toán] Chưa rõ điểm khó chịu nhất khi làm việc bằng 2 file Excel là gì (phải sửa "
            + "tay ở 2 file, không biết JD nào đang gán cho ai, người khác muốn xem phải hỏi HRBP, hay file "
            + "dễ sửa nhầm không biết ai sửa)",
            "[Luồng ngoại lệ & trường hợp đặc biệt] Chưa rõ có trường hợp ngoại lệ nào không, ví dụ JD bị trùng tên"
        };

        var kept = CoverageStaleGapGuard.DropAnsweredItems(QuyTrinhHienTai, pending);

        Assert.Single(kept);
        Assert.Contains("trùng tên", kept[0], StringComparison.Ordinal);
    }
}
