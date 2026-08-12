using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// BẢNG PHÂN QUYỀN là chỗ người dùng chốt "vai nào làm được gì, trên phạm vi dữ liệu nào". Nó thay cho một
// câu hỏi mà thực tế không ai trả lời nổi ("mỗi vai trò được xem và thao tác những gì?") — câu đó nhận về
// "cứ vậy đã, có gì tôi bổ sung sau", rồi BA tự soạn phương án và một chip "Đồng ý" đóng dấu [RÕ] cho cả
// nhóm phân quyền, khóa luôn đường hỏi lại.
//
// Bảng chỉ trả được cái giá đó nếu ba bất biến dưới đây đứng vững, và model không giữ được chúng bằng
// thiện chí. Các test này là cái phanh:
//
//   • dòng phải trỏ vào MÀN HÌNH CÓ THẬT trong phạm vi đã chắt — dòng bịa là một tính năng ngoài phạm vi
//     đi thẳng vào tài liệu mang chữ ký người dùng;
//   • màn hình BỊ BỎ QUÊN vẫn phải có mặt — vắng mặt thì nó mặc nhiên "không ai có quyền" mà người dùng
//     không bao giờ nhìn thấy để phản đối, đúng khiếm khuyết của hàng chip mà bảng sinh ra để chữa;
//   • ô chỉ được KHÓA khi có trích dẫn thật — nếu không, một bảng điền sẵn trông như đã chốt chính là cái
//     chip "Đồng ý phương án này" phóng to.
public class PermissionMatrixBuilderTests
{
    private static readonly List<string> Scope = new()
    {
        "Màn hình Training Plan để tạo và quản lý kế hoạch cho cả năm",
        "Màn hình Training Implement để quản lý lịch học và mã lớp",
        "Màn hình Admin xem xét và xử lý các ticket ở trạng thái waitlist"
    };

    private static PermissionMatrixRow Row(string screen, string function, params (string Role, string Scope, string Evidence)[] grants)
        => new()
        {
            Screen = screen,
            Function = function,
            Grants = grants.Select(g => new PermissionGrant { Role = g.Role, Scope = g.Scope, Evidence = g.Evidence }).ToList()
        };

    [Fact]
    public void Build_KeepsRowsThatMatchThePlannedScope()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row(Scope[0], "Xem", ("HR Assistant", "của mình", ""), ("HOD HR", "tất cả", ""))
        }, Scope);

        var plan = rows.Single(r => r.Screen == Scope[0] && r.Function == "Xem");
        Assert.Equal("của mình", plan.Grants.Single(g => g.Role == "HR Assistant").Scope);
        Assert.Equal("tất cả", plan.Grants.Single(g => g.Role == "HOD HR").Scope);
    }

    // Model hay rút gọn tên màn hình ("Màn hình Training Plan") thay vì chép nguyên mục phạm vi. Vẫn ghép
    // được, nhưng tên đi vào bảng luôn là chữ của PHẠM VI — người dùng đang đối chiếu với bản tổng kết họ
    // vừa đọc, và bước sinh tài liệu sau đó ghép màn hình theo đúng tên đó.
    [Fact]
    public void Build_MatchesAShortenedScreenName_ButKeepsTheScopeWording()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row("Màn hình Training Plan", "Xem", ("HR Assistant", "của mình", ""))
        }, Scope);

        Assert.Contains(rows, r => r.Screen == Scope[0] && r.Function == "Xem");
        Assert.DoesNotContain(rows, r => r.Screen == "Màn hình Training Plan");
    }

    [Fact]
    public void Build_DropsScreensThatAreNotInThePlannedScope()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row(Scope[0], "Xem", ("HR Assistant", "của mình", "")),
            Row("Màn hình báo cáo ngân sách đào tạo", "Xem", ("HR Assistant", "tất cả", ""))
        }, Scope);

        Assert.DoesNotContain(rows, r => r.Screen.Contains("ngân sách"));
    }

    // Màn hình model quên nhắc tới vẫn phải xuất hiện, ở trạng thái chưa ai có quyền: đây là vế mà hàng
    // chip không bao giờ nói ra, và cũng là lý do bảng tồn tại.
    [Fact]
    public void Build_AddsEveryPlannedScreenThatTheModelForgot()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row(Scope[0], "Xem", ("HR Assistant", "của mình", ""))
        }, Scope);

        var forgotten = rows.Single(r => r.Screen == Scope[2]);
        Assert.Equal("Xem", forgotten.Function);
        Assert.All(forgotten.Grants, g => Assert.False(PermissionScope.IsGranted(g.Scope)));
        Assert.Equal(Scope, rows.Select(r => r.Screen).Distinct());
    }

    // Vai chỉ được nêu ở một dòng vẫn phải có Ô ở mọi dòng còn lại. Ô vắng mặt không phải "không có quyền"
    // mà là "không hỏi" — và người dùng không có cách nào phân biệt hai thứ đó trên màn hình.
    [Fact]
    public void Build_GivesEveryRowTheFullSetOfRoles()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row(Scope[0], "Xem", ("HR Assistant", "của mình", "")),
            Row(Scope[1], "Duyệt/Từ chối", ("HOD HR", "tất cả", "")),
            Row(Scope[2], "Xử lý waitlist", ("Admin", "tất cả", ""))
        }, Scope);

        Assert.All(rows, r => Assert.Equal(
            new[] { "HR Assistant", "HOD HR", "Admin" },
            r.Grants.Select(g => g.Role)));
    }

    // LUẬT BẰNG CHỨNG. Khóa một ô là tuyên bố "người dùng đã tự nói điều này" — server không nhận tuyên bố
    // đó từ một lá cờ, phải có trích dẫn kèm theo. Đây là thứ giữ cho bảng khỏi biến thành một cái chip
    // "Đồng ý phương án này" to hơn.
    [Fact]
    public void Build_LocksOnlyTheCellsThatCarryEvidence()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row(Scope[0], "Xem",
                ("HR Assistant", "của mình", "Assistant được xem và chỉnh các Training Plan do mình lập"),
                ("HOD HR", "tất cả", ""))
        }, Scope);

        var grants = rows.Single(r => r.Screen == Scope[0] && r.Function == "Xem").Grants;
        Assert.True(grants.Single(g => g.Role == "HR Assistant").Locked);
        // BA vẫn được đề xuất phạm vi cho ô này — chỉ là nó KHÔNG được khóa, nên người dùng vẫn phải tự quyết.
        Assert.False(grants.Single(g => g.Role == "HOD HR").Locked);
        Assert.Equal("tất cả", grants.Single(g => g.Role == "HOD HR").Scope);
    }

    [Theory]
    [InlineData("own", "của mình")]
    [InlineData("chỉ của mình", "của mình")]
    [InlineData("nhân viên thuộc quyền", "của đơn vị")]
    [InlineData("toàn bộ", "tất cả")]
    [InlineData("không", "")]
    [InlineData("", "")]
    public void Build_NormalizesWhateverTheModelWroteIntoTheFourScopeSteps(string raw, string expected)
    {
        var rows = PermissionMatrixBuilder.Build(new[] { Row(Scope[0], "Xem", ("HR Assistant", raw, "")) }, Scope);

        Assert.Equal(expected, rows.First().Grants.Single(g => g.Role == "HR Assistant").Scope);
    }

    // Bảng chỉ được dựng khi có phạm vi để dựng trên đó. Phạm vi trống ⇒ rỗng, và caller fail-open sang
    // lượt chat thường thay vì bày một bảng không có dòng nào.
    [Fact]
    public void Build_ReturnsEmpty_WhenThereIsNoScopeOrNoUsableProposal()
    {
        Assert.Empty(PermissionMatrixBuilder.Build(new[] { Row(Scope[0], "Xem", ("HR Assistant", "tất cả", "")) }, new List<string>()));
        Assert.Empty(PermissionMatrixBuilder.Build(Array.Empty<PermissionMatrixRow>(), Scope));
        // Có dòng nhưng không vai nào ⇒ bảng không có cột: vô nghĩa, đừng bày ra.
        Assert.Empty(PermissionMatrixBuilder.Build(new[] { Row(Scope[0], "Xem") }, Scope));
    }

    // Bảng người dùng GỬI LÊN: mọi ô đều là quyết định của họ, nên cờ "đã có bằng chứng trong hội thoại"
    // (chỉ dùng để phân biệt lúc bảng còn chờ) được xoá sạch — giữ lại là để một trích dẫn của BA sống
    // tiếp trong dữ liệu đã chốt.
    [Fact]
    public void Sanitize_ClearsTheEvidenceFlags_AndStillGuardsTheScreenNames()
    {
        var rows = PermissionMatrixBuilder.Sanitize(new[]
        {
            Row(Scope[0], "Xem", ("HR Assistant", "của mình", "trích dẫn cũ")),
            Row("Màn hình bịa", "Xem", ("HR Assistant", "tất cả", ""))
        }, Scope);

        Assert.All(rows.SelectMany(r => r.Grants), g =>
        {
            Assert.False(g.Locked);
            Assert.Equal(string.Empty, g.Evidence);
        });
        Assert.DoesNotContain(rows, r => r.Screen == "Màn hình bịa");
    }

    // Khối ngữ cảnh là thứ DUY NHẤT chở bảng tới BA ở các lượt sau, tới lượt distill bản đồ bao phủ và tới
    // prompt sinh spec. Phạm vi phải đi cùng vai trò trong đó: "xem Training Plan" và "xem Training Plan
    // do mình lập" là hai yêu cầu khác hẳn nhau.
    [Fact]
    public void RenderConfirmedBlock_CarriesRoleScopeAndCondition()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            new PermissionMatrixRow
            {
                Screen = Scope[0],
                Function = "Sửa",
                Condition = "chỉ sửa khi chưa submit",
                Grants = new List<PermissionGrant>
                {
                    new() { Role = "HR Assistant", Scope = "của mình" },
                    new() { Role = "Nhân viên", Scope = "" }
                }
            }
        }, Scope);

        var block = PermissionMatrixBuilder.RenderConfirmedBlock(System.Text.Json.JsonSerializer.Serialize(rows))!;

        Assert.Contains("HR Assistant (của mình)", block);
        Assert.Contains("chỉ sửa khi chưa submit", block);
        Assert.DoesNotContain("Nhân viên (", block);
        // Màn hình chưa cấp quyền cho ai vẫn phải nói ra — đó cũng là một quyết định của người dùng.
        Assert.Contains("KHÔNG vai nào", block);
        Assert.Null(PermissionMatrixBuilder.RenderConfirmedBlock(null));
    }

    // Tin nhắn gửi vào hội thoại: mọi tầng chắt lọc (bản đồ bao phủ, nhật ký điều đã chốt, Product Brief)
    // đọc transcript, nên bảng phải đi vào đó ở dạng đọc được chứ không phải một câu "đã chốt bảng".
    [Fact]
    public void RenderUserMessage_NamesTheRowsThatNobodyCanDo()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row(Scope[0], "Xem", ("HR Assistant", "của mình", "")),
            Row(Scope[0], "Xóa", ("HR Assistant", "", ""))
        }, Scope);

        var message = PermissionMatrixBuilder.RenderUserMessage(rows);

        Assert.Contains("Xem: HR Assistant (của mình)", message);
        Assert.Contains("Xóa: không vai nào được làm", message);
    }
}
