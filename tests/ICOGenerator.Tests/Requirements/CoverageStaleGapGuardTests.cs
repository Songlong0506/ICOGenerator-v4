using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Một nhóm không được vừa ghi nhận một điều vừa giữ một câu hỏi về chính điều đó.
//
// Ca thật (dự án JD Libary 4, buổi 24 lượt) — cả bốn dòng dưới đây lấy nguyên văn từ bản xuất hội thoại:
//
//   - Người dùng trả lời điểm đau ở lượt 5. Distiller ghi trọn bốn điểm đó vào dòng «Quy trình hiện tại &
//     điểm khó» ở [RÕ], nhưng nhóm «Mục tiêu / bài toán» vẫn giữ câu hỏi "Chưa rõ điểm khó chịu nhất…"
//     suốt 19 lượt sau đó.
//   - Nhóm «Quy tắc nghiệp vụ & ràng buộc» liệt kê đủ ba quy tắc rồi vẫn kèm câu hỏi "Chưa rõ các quy tắc
//     bắt buộc … (ví dụ mã JD duy nhất, Responsibility tổng % bằng 100)" — đúng ba quy tắc nó vừa ghi.
//
// Thiệt hại là một VÒNG LẶP KÍN, không phải một dòng xấu: RequirementReadinessGate lấy nguyên câu đó làm
// câu chặn, nên lượt 24 của buổi ấy là câu hỏi của lượt 4 phát lại nguyên văn — thứ người dùng đã trả lời
// 19 lượt trước. Distiller lại chép mục cũ sang lượt sau, và nút "Write Requirement" khoá vĩnh viễn.
public class CoverageStaleGapGuardTests
{
    // Một dòng bullet của fixture chở cả hai nửa mà production lưu ở hai cột: phần trước "còn thiếu:" là
    // dòng bản đồ, phần sau là câu hỏi của nhóm ấy. Guard nhận cả hai và sửa TẠI CHỖ.
    private static (List<CoverageMapItem> Items, List<OpenQuestionEntry> Questions) Apply(string bullets)
    {
        var items = CoverageMapFixture.Items(bullets).ToList();
        var questions = CoverageMapFixture.Questions(bullets);
        CoverageStaleGapGuard.Apply(items, questions);
        return (items, questions);
    }

    private static CoverageMapItem Row(IEnumerable<CoverageMapItem> items, string labelPrefix) =>
        items.First(x => x.Label.StartsWith(labelPrefix, StringComparison.Ordinal));

    // Nguyên văn hai dòng của buổi JD Libary 4.
    private static readonly string MucTieu =
        ("- ★ Mục tiêu / bài toán: [MỘT PHẦN] App quản lý danh sách JD trong nhà máy và gán JD cho nhân viên. "
        + "còn thiếu: Chưa rõ điểm khó chịu nhất khi làm việc bằng 2 file Excel là gì (phải sửa tay ở 2 file, "
        + "không biết JD nào đang gán cho ai, người khác muốn xem phải hỏi HRBP, hay file dễ sửa nhầm không "
        + "biết ai sửa) {nguồn: \"đây là app để quản lý danh sách JD ở trong nhà máy\"}");

    private static readonly string QuyTrinhHienTai =
        ("- Quy trình hiện tại & điểm khó: [RÕ] Hiện tại dùng 2 file Excel (1 file danh sách JD, 1 file JD gán "
        + "cho nhân viên), HRBP tự thao tác. Điểm khó: sửa tay 2 file, không biết JD nào đang gán cho ai, "
        + "người khác xem phải hỏi HRBP, dễ sửa nhầm không biết ai sửa. {nguồn: \"tất cả các thông tin mà bạn gợi ý ở trên\"}");

    [Fact]
    public void AQuestionAnsweredByAnotherClearRow_IsDropped()
    {
        var (items, questions) = Apply(MucTieu + "\n" + QuyTrinhHienTai);

        Assert.Empty(questions);
        var row = Row(items, "Mục tiêu");
        // Trạng thái, phần đã ghi nhận, bằng chứng và cờ ★ của dòng đều còn nguyên — guard chỉ xoá câu chết.
        Assert.Equal("MỘT PHẦN", row.Status);
        Assert.StartsWith("App quản lý danh sách JD", row.Known, StringComparison.Ordinal);
        Assert.Equal("\"đây là app để quản lý danh sách JD ở trong nhà máy\"", row.Evidence);
        Assert.True(row.IsCore);
    }

    [Fact]
    public void AQuestionAnsweredByItsOwnRow_IsDropped()
    {
        var (items, questions) = Apply(
            ("- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Mã JD phải duy nhất; Responsibility phải có tổng % "
            + "bằng 100; tất cả các trường của JD (mã JD, OrgUnit, JobTitle, JobFunction, PC Level, Skill, "
            + "Degree, Major, Responsibility) là bắt buộc, không được để trống. còn thiếu: Chưa rõ các quy tắc "
            + "bắt buộc cho các trường thông tin JD (ví dụ mã JD duy nhất, Responsibility tổng % bằng 100) "
            + "{nguồn: \"mã JD phải duy nhất, Responsibility phải có tổng % bằng 100\"}"));

        Assert.Empty(questions);
        var row = Row(items, "Quy tắc nghiệp vụ");
        Assert.Equal("MỘT PHẦN", row.Status);
        Assert.StartsWith("Mã JD phải duy nhất", row.Known, StringComparison.Ordinal);
    }

    // Cắt vòng lặp là đủ; guard KHÔNG được tự kết luận "vậy là đã đủ", cũng không đánh dấu câu ấy ĐÃ TRẢ
    // LỜI. Bằng chứng ở đây do LLM chắt, khác CoverageConfirmedTableGuard nơi bằng chứng là từng ô người
    // dùng tự tay bấm. Dòng mất câu hỏi vẫn [MỘT PHẦN] và cổng rơi về nhánh PHÁT LẠI — một câu hỏi ĐÓNG LẠI
    // ĐƯỢC bằng một lượt, thay cho một câu hỏi mà người dùng đã trả lời rồi.
    [Fact]
    public void TheRowStaysPartial_AndTheGateAsksAQuestionThatCanBeClosed()
    {
        var (items, questions) = Apply(MucTieu + "\n" + QuyTrinhHienTai);

        var readiness = RequirementReadinessGate.Evaluate(CoverageMapParser.Serialize(items), questions);

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
    public void AQuestionTheRowDoesNotActuallyAnswer_IsKept(string line)
    {
        Assert.NotEmpty(Apply(line).Questions);
    }

    // Cụm tín hiệu tái mở là một LỆNH của người dùng ("nhóm này BA hiểu sai, hỏi lại giúp tôi"), không phải
    // một câu hỏi chết. Xoá nó là bịt đúng đường họ vừa mở — và AskedQuestionHistory.ReopenedGroups đọc
    // chính cụm đó để miễn phanh chống hỏi lại cho nhóm ấy.
    [Fact]
    public void TheReopenMarker_IsNeverDropped()
    {
        var questions = Apply(
            "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] Manager tạo JD, HRBP duyệt, HoD duyệt cuối. "
            + "còn thiếu: " + AskedQuestionHistory.ReopenNote + " — cần hỏi lại và chốt lại. Manager tạo JD, "
            + "HRBP duyệt, HoD duyệt cuối.").Questions;

        Assert.Contains(AskedQuestionHistory.ReopenNote, Assert.Single(questions).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingToRepair_LeavesBothListsAlone()
    {
        var (items, questions) = Apply(QuyTrinhHienTai);
        Assert.Empty(questions);
        Assert.Equal("RÕ", Assert.Single(items).Status);

        var empty = new List<OpenQuestionEntry>();
        CoverageStaleGapGuard.Apply(Array.Empty<CoverageMapItem>(), empty);
        Assert.Empty(empty);
    }

    // Câu hỏi của một nhóm KHÁC vẫn bị soi: kho lời giải là phần đã ghi nhận của MỌI dòng [RÕ], vì
    // distiller hay ghi câu trả lời vào đúng nhóm của nó rồi để câu hỏi nằm lại ở nhóm đã hỏi ra nó.
    [Fact]
    public void OnlyTheAnsweredOneIsDropped_TheRealQuestionSurvives()
    {
        var items = CoverageMapFixture.Items(QuyTrinhHienTai).ToList();
        var questions = OpenQuestionFixture.Items(
            "[Mục tiêu / bài toán] Chưa rõ điểm khó chịu nhất khi làm việc bằng 2 file Excel là gì (phải sửa "
            + "tay ở 2 file, không biết JD nào đang gán cho ai, người khác muốn xem phải hỏi HRBP, hay file "
            + "dễ sửa nhầm không biết ai sửa)",
            "[Luồng ngoại lệ & trường hợp đặc biệt] Chưa rõ có trường hợp ngoại lệ nào không, ví dụ JD bị trùng tên")
            .ToList();

        CoverageStaleGapGuard.Apply(items, questions);

        Assert.Single(questions);
        Assert.Contains("trùng tên", questions[0].Text, StringComparison.Ordinal);
    }
}
