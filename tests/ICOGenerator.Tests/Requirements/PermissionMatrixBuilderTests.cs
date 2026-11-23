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
//   • dòng LÀ tác vụ của bản thiết kế UX, khoá theo ID — dòng trỏ vào tác vụ không có thật là một tính năng
//     ngoài phạm vi đi thẳng vào tài liệu mang chữ ký người dùng;
//   • tác vụ BỊ BỎ QUÊN vẫn phải có dòng — vắng mặt thì nó mặc nhiên "không ai có quyền" mà người dùng
//     không bao giờ nhìn thấy để phản đối, đúng khiếm khuyết của hàng chip mà bảng sinh ra để chữa;
//   • ô chỉ được KHÓA khi có trích dẫn thật — nếu không, một bảng điền sẵn trông như đã chốt chính là cái
//     chip "Đồng ý phương án này" phóng to.
public class PermissionMatrixBuilderTests
{
    // Ba tác vụ trên hai màn: thứ tự dòng của bảng là thứ tự màn hình (T-02 đứng đầu vì màn đầu đặt nó).
    private static readonly UxBlueprint Blueprint = new()
    {
        Tasks =
        {
            new UxTask { Id = "T-01", Name = "Xem kế hoạch đào tạo" },
            new UxTask { Id = "T-02", Name = "Duyệt kế hoạch" },
            new UxTask { Id = "T-03", Name = "Xử lý waitlist" }
        },
        Screens =
        {
            new UxScreen { Id = "S-01", Name = "Plan Approval", Actions = { new UxAction { Task = "T-02" } } },
            new UxScreen { Id = "S-02", Name = "Training Plans", Actions = { new UxAction { Task = "T-01" }, new UxAction { Task = "T-03" } } }
        }
    };

    private static PermissionMatrixRow Row(string taskId, params (string Role, string Scope, string Evidence)[] grants)
        => new()
        {
            TaskId = taskId,
            Function = "chữ model gõ",
            Screen = "màn model đoán",
            Grants = grants.Select(g => new PermissionGrant { Role = g.Role, Scope = g.Scope, Evidence = g.Evidence }).ToList()
        };

    // PHẠM VI KHÔNG ĐỌC ĐƯỢC ⇒ Ô TRỐNG, không phải nấc rộng nhất.
    //
    // Bản trước cho mọi chuỗi lạ rơi về "tất cả": model viết lệch một câu ("chỉ bản ghi liên quan") là ô
    // đó thành quyền toàn hệ thống trong bảng người dùng sắp bấm gửi — mà chính luật của bảng này nói ô
    // BA suy đoán thì để trống cho người dùng tự chọn. Ô trống hiện thành ô chọn còn nguyên, nên cái giá
    // của hướng an toàn chỉ là một cú tích; hướng cũ thì cái giá là một quyền rộng hơn thực tế không ai
    // gõ ra.
    [Theory]
    [InlineData("chỉ bản ghi liên quan")]
    [InlineData("tùy trường hợp")]
    [InlineData("x")]
    public void Build_LeavesAnUnreadableScopeEmpty(string scope)
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row("T-01", ("HR Assistant", scope, ""))
        }, Blueprint);

        var grant = rows.Single(r => r.TaskId == "T-01")
            .Grants.Single(g => g.Role == "HR Assistant");
        Assert.Equal(PermissionScope.None, grant.Scope);
        Assert.False(PermissionScope.IsGranted(grant.Scope));
    }

    // Nhưng nấc rộng nhất vẫn phải đọc được khi model GỌI TÊN nó — nó không còn là chỗ mọi chuỗi lạ rơi
    // vào, nên phải có bảng cụm từ của riêng mình.
    [Theory]
    [InlineData("tất cả")]
    [InlineData("Toàn bộ")]
    [InlineData("all")]
    public void Build_StillReadsTheWidestScopeWhenItIsNamed(string scope)
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row("T-01", ("HOD HR", scope, ""))
        }, Blueprint);

        Assert.Equal(PermissionScope.All, rows
            .Single(r => r.TaskId == "T-01")
            .Grants.Single(g => g.Role == "HOD HR").Scope);
    }

    // Ba giá trị hợp lệ là một giao ước với prompt: permission-matrix-draft.v1.md kê đúng chúng cho model.
    // Đổi hằng số mà quên prompt thì model viết một chuỗi không còn ai đọc được, và từ nay chuỗi đó thành
    // ô trống — im lặng, chỉ là bảng bỗng dưng trống trơn.
    [Fact]
    public void PermissionPromptListsTheExactScopeValues()
    {
        var prompt = PromptFixture.Read(PermissionMatrixDraftService.PromptKey);

        Assert.Contains(PermissionScope.Own, prompt, StringComparison.Ordinal);
        Assert.Contains(PermissionScope.Unit, prompt, StringComparison.Ordinal);
        Assert.Contains(PermissionScope.All, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_KeepsRowsThatMatchATask()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row("T-01", ("HR Assistant", "của mình", ""), ("HOD HR", "tất cả", ""))
        }, Blueprint);

        var plan = rows.Single(r => r.TaskId == "T-01");
        Assert.Equal(PermissionScope.Own, plan.Grants.Single(g => g.Role == "HR Assistant").Scope);
        Assert.Equal(PermissionScope.All, plan.Grants.Single(g => g.Role == "HOD HR").Scope);
    }

    // Model quên ID nhưng chép đúng TÊN tác vụ ⇒ vẫn ghép được vào đúng dòng.
    [Fact]
    public void Build_MatchesARowByTaskName_WhenTheModelForgotTheId()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            new PermissionMatrixRow { Function = "Duyệt kế hoạch", Grants = { new PermissionGrant { Role = "HOD HR", Scope = "tất cả" } } }
        }, Blueprint);

        Assert.Equal(PermissionScope.All, rows.Single(r => r.TaskId == "T-02").Grants.Single().Scope);
    }

    [Fact]
    public void Build_DropsRowsForTasksThatDoNotExist()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row("T-01", ("HR Assistant", "của mình", "")),
            Row("T-99", ("HR Assistant", "tất cả", ""))
        }, Blueprint);

        Assert.DoesNotContain(rows, r => r.TaskId == "T-99");
        Assert.Equal(new[] { "T-02", "T-01", "T-03" }, rows.Select(r => r.TaskId));
    }

    // Tác vụ model quên nhắc tới vẫn phải có dòng, ở trạng thái chưa ai có quyền: đây là vế mà hàng chip
    // không bao giờ nói ra, và cũng là lý do bảng tồn tại.
    [Fact]
    public void Build_AddsEveryTaskThatTheModelForgot()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row("T-01", ("HR Assistant", "của mình", ""))
        }, Blueprint);

        var forgotten = rows.Single(r => r.TaskId == "T-03");
        Assert.Equal("Xử lý waitlist", forgotten.Function);
        Assert.All(forgotten.Grants, g => Assert.False(PermissionScope.IsGranted(g.Scope)));
        Assert.Equal(3, rows.Count);
    }

    // Đường BỔ SUNG dòng cho tác vụ mới: dòng đang lưu (người dùng có thể đã sửa ở popup) thắng mọi đề xuất.
    [Fact]
    public void Build_KeepsTheSavedRows_OverAnyProposal()
    {
        var rows = PermissionMatrixBuilder.Build(
            new[] { Row("T-01", ("HR Assistant", "tất cả", "")), Row("T-03", ("HR Assistant", "của mình", "")) },
            Blueprint,
            keep: new[] { Row("T-01", ("HR Assistant", "của đơn vị", "")) });

        Assert.Equal(PermissionScope.Unit, rows.Single(r => r.TaskId == "T-01").Grants.Single().Scope);
        Assert.Equal(PermissionScope.Own, rows.Single(r => r.TaskId == "T-03").Grants.Single().Scope);
    }

    // Vai chỉ được nêu ở một dòng vẫn phải có Ô ở mọi dòng còn lại. Ô vắng mặt không phải "không có quyền"
    // mà là "không hỏi" — và người dùng không có cách nào phân biệt hai thứ đó trên màn hình.
    [Fact]
    public void Build_GivesEveryRowTheFullSetOfRoles()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row("T-01", ("HR Assistant", "của mình", "")),
            Row("T-02", ("HOD HR", "tất cả", "")),
            Row("T-03", ("Admin", "tất cả", ""))
        }, Blueprint);

        Assert.All(rows, r => Assert.Equal(
            new[] { "HR Assistant", "HOD HR", "Admin" },
            r.Grants.Select(g => g.Role)));
    }

    // BỘ CỘT do người dùng rà trên bảng "Vai trò" THẮNG bộ chắt từ grants. Không có luật này thì bảng vai
    // trò chỉ là một màn bấm đẹp: vai vừa thêm mà chưa cấp quyền ở dòng nào sẽ không có cột nào, còn vai vừa
    // xóa vẫn sống tiếp nhờ grants cũ trong payload.
    [Fact]
    public void Build_TakesTheColumnsFromTheRoleList_WhenOneIsGiven()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row("T-01", ("HR Assistant", "của mình", ""), ("HOD HR", "tất cả", ""))
        }, Blueprint, new[] { "HR Assistant", "Admin" });

        Assert.All(rows, r => Assert.Equal(new[] { "HR Assistant", "Admin" }, r.Grants.Select(g => g.Role)));

        var view = rows.Single(r => r.TaskId == "T-01");
        // Vai người dùng thêm: có cột ở mọi dòng, chưa có quyền. Vai người dùng xóa: không còn ô nào.
        Assert.False(PermissionScope.IsGranted(view.Grants.Single(g => g.Role == "Admin").Scope));
        Assert.Equal(PermissionScope.Own, view.Grants.Single(g => g.Role == "HR Assistant").Scope);
    }

    // Danh sách rỗng KHÔNG có nghĩa "bảng không có cột" — đó là lượt BA bày bảng (bảng vai trò chưa tồn
    // tại) và các tab mở từ trước bản này. Rơi về bản chắt từ grants như cũ.
    [Fact]
    public void Build_FallsBackToTheProposedRoles_WhenTheRoleListIsEmpty()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row("T-01", ("HR Assistant", "của mình", ""))
        }, Blueprint, Array.Empty<string>());

        Assert.Equal(new[] { "HR Assistant" }, rows.First().Grants.Select(g => g.Role));
    }

    // Cùng luật với bản JS đọc bảng vai trò (permRoleValues): lệch nhau là người dùng thấy một cột trên màn
    // hình rồi bị server bỏ đúng cột đó.
    [Fact]
    public void SanitizeRoles_DropsBlanksAndDuplicates_AndStopsAtTheColumnCap()
    {
        Assert.Equal(
            new[] { "HRBP", "Manager" },
            PermissionMatrixBuilder.SanitizeRoles(new[] { "  ", "HRBP", "hrbp ", "Manager", "" }));

        // "HRBP" và "hrbp " là hai dòng khác nhau trên bảng nhưng cùng MỘT cột lúc so khớp.
        Assert.Equal(
            PermissionMatrixBuilder.MaxRoles,
            PermissionMatrixBuilder.SanitizeRoles(Enumerable.Range(1, 20).Select(i => $"Vai {i}")).Count);
    }

    // LUẬT BẰNG CHỨNG. Khóa một ô là tuyên bố "người dùng đã tự nói điều này" — server không nhận tuyên bố
    // đó từ một lá cờ, phải có trích dẫn kèm theo. Đây là thứ giữ cho bảng khỏi biến thành một cái chip
    // "Đồng ý phương án này" to hơn.
    [Fact]
    public void Build_LocksOnlyTheCellsThatCarryEvidence()
    {
        var rows = PermissionMatrixBuilder.Build(new[]
        {
            Row("T-01",
                ("HR Assistant", "của mình", "Assistant được xem và chỉnh các Training Plan do mình lập"),
                ("HOD HR", "tất cả", ""))
        }, Blueprint);

        var grants = rows.Single(r => r.TaskId == "T-01").Grants;
        Assert.True(grants.Single(g => g.Role == "HR Assistant").Locked);
        // BA vẫn được đề xuất phạm vi cho ô này — chỉ là nó KHÔNG được khóa, nên người dùng vẫn phải tự quyết.
        Assert.False(grants.Single(g => g.Role == "HOD HR").Locked);
        Assert.Equal(PermissionScope.All, grants.Single(g => g.Role == "HOD HR").Scope);
    }

    [Theory]
    [InlineData("own", PermissionScope.Own)]
    [InlineData("chỉ của mình", PermissionScope.Own)]
    [InlineData("nhân viên thuộc quyền", PermissionScope.Unit)]
    [InlineData("toàn bộ", PermissionScope.All)]
    [InlineData("không", "")]
    [InlineData("", "")]
    public void Build_NormalizesWhateverTheModelWroteIntoTheFourScopeSteps(string raw, string expected)
    {
        var rows = PermissionMatrixBuilder.Build(new[] { Row("T-01", ("HR Assistant", raw, "")) }, Blueprint);

        Assert.Equal(expected, rows.Single(r => r.TaskId == "T-01").Grants.Single(g => g.Role == "HR Assistant").Scope);
    }

    // Bảng chỉ được dựng khi có tác vụ để dựng trên đó và có ít nhất một vai. Không ⇒ rỗng, caller fail-open.
    [Fact]
    public void Build_ReturnsEmpty_WhenThereAreNoTasksOrNoRoles()
    {
        Assert.Empty(PermissionMatrixBuilder.Build(new[] { Row("T-01", ("HR Assistant", "tất cả", "")) }, new UxBlueprint()));
        Assert.Empty(PermissionMatrixBuilder.Build(Array.Empty<PermissionMatrixRow>(), Blueprint));
        // Có dòng nhưng không vai nào ⇒ bảng không có cột: vô nghĩa.
        Assert.Empty(PermissionMatrixBuilder.Build(new[] { Row("T-01") }, Blueprint));
    }

    // Bảng người dùng GỬI LÊN: mọi ô đều là quyết định của họ, nên cờ "đã có bằng chứng trong hội thoại"
    // (chỉ dùng để phân biệt lúc bảng còn chờ) được xoá sạch — giữ lại là để một trích dẫn của BA sống
    // tiếp trong dữ liệu đã chốt.
    [Fact]
    public void Sanitize_ClearsTheEvidenceFlags_AndStillGuardsTheTasks()
    {
        var rows = PermissionMatrixBuilder.Sanitize(new[]
        {
            Row("T-01", ("HR Assistant", "của mình", "trích dẫn cũ")),
            Row("T-77", ("HR Assistant", "tất cả", ""))
        }, Blueprint);

        Assert.All(rows.SelectMany(r => r.Grants), g =>
        {
            Assert.False(g.Locked);
            Assert.Equal(string.Empty, g.Evidence);
        });
        Assert.DoesNotContain(rows, r => r.TaskId == "T-77");
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
                TaskId = "T-01",
                Condition = "chỉ sửa khi chưa submit",
                Grants = new List<PermissionGrant>
                {
                    new() { Role = "HR Assistant", Scope = "của mình" },
                    new() { Role = "Nhân viên", Scope = "" }
                }
            }
        }, Blueprint);

        var block = PermissionMatrixBuilder.RenderConfirmedBlock(System.Text.Json.JsonSerializer.Serialize(rows))!;

        Assert.Contains("HR Assistant (của mình)", block);
        Assert.Contains("chỉ sửa khi chưa submit", block);
        Assert.DoesNotContain("Nhân viên (", block);
        // Màn hình chưa cấp quyền cho ai vẫn phải nói ra — đó cũng là một quyết định của người dùng.
        Assert.Contains("KHÔNG vai nào", block);
        Assert.Null(PermissionMatrixBuilder.RenderConfirmedBlock(null));
    }
}
