using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng trả lời "trên màn hình còn bảng chốt nào đang chờ người dùng gửi không". Nó sinh ra từ một ca thật:
// bản Product Brief đã có, BA bày một BẢNG ra và nói "rà lại rồi bấm Gửi bảng … giúp mình" — nhưng nút
// "Write Requirement" cũng đang sáng ngay bên dưới cái bảng đó (hồi đó cổng còn mở theo đường "đã có draft +
// bản đồ đã đủ" không đọc lượt cuối — nay đã gỡ, nhưng ca fail-open vẫn còn: bảng của lượt trước treo, lượt
// này model mời). Người dùng bấm nút, và vòng soạn chạy trên một hội thoại mà
// bảng còn chưa chốt ⇒ tài liệu ra đời thiếu phần của bảng đó, rồi tin nhắn chốt bảng lại mở cổng lần nữa
// và vòng soạn thứ hai ghi đè lên bản vừa sinh.
//
// Ba chỗ đọc cổng này (Index.cshtml, requirements.js, ProductBriefDraftService) nên luật phải nằm ở MỘT
// hàm — hai bản chép tay thì lần sửa sau chỉ sửa một bản.
public class PendingConfirmTableGateTests
{
    private const string OneFlow = """
        [{"name":"Lập JD","kind":"luồng chính","role":"Manager","steps":[
          {"actor":"Manager","action":"Tạo JD","outcome":"Nháp","included":true},
          {"actor":"HRBP","action":"Duyệt JD","outcome":"Đã duyệt","included":true}]}]
        """;

    private static Project ProjectWith(params AgentConversation[] turns)
    {
        var project = new Project { Name = "JD Library" };
        foreach (var turn in turns)
            project.Conversations.Add(turn);
        return project;
    }

    private static AgentConversation BaTurn(int minute, Action<AgentConversation> fill)
    {
        var turn = new AgentConversation
        {
            Role = "assistant",
            Message = "…",
            CreatedAt = new DateTime(2026, 1, 1, 9, minute, 0, DateTimeKind.Utc)
        };
        fill(turn);
        return turn;
    }

    [Fact]
    public void Select_ReturnsNull_WhenNoTableIsWaiting()
    {
        Assert.Null(PendingConfirmTableGate.Select(ProjectWith(BaTurn(1, _ => { }))));
    }

    // Hình dạng của ca thật ở đầu file: lượt BA vừa bày bảng luồng, cột trên Project còn null ⇒ bảng vẫn đang chờ.
    [Fact]
    public void Select_NamesTheFlowTable_WhileItIsStillUnsent()
    {
        var project = ProjectWith(BaTurn(1, t => t.FlowMap = OneFlow));

        var pending = PendingConfirmTableGate.Select(project);

        Assert.Equal(PendingConfirmTableGate.FlowMap, pending);
        // Câu chặn phải gọi tên ĐÚNG cái nút người dùng cần bấm: cổng đóng mà không chỉ được đường nào thì
        // chỉ là một nút mờ-và-khóa không có nút.
        Assert.Contains("bảng luồng", pending!.GateHint);
        Assert.Contains("Gửi bảng luồng", pending.GateHint);
        Assert.Contains("Gửi bảng luồng", pending.BlockedTurn);
    }

    // Gửi xong ⇒ cột trên Project khác null ⇒ cổng nhả ngay, không cần lượt chat nào. Thiếu vế này là khóa
    // chết nút tạo tài liệu bằng đúng cái bảng người dùng vừa chốt.
    [Fact]
    public void Select_LetsGo_OnceTheProjectColumnIsFilled()
    {
        var project = ProjectWith(BaTurn(1, t => t.FlowMap = OneFlow));
        project.FlowMap = OneFlow;

        Assert.Null(PendingConfirmTableGate.Select(project));
    }

    // Bảng treo theo DỰ ÁN chứ không theo lượt: người dùng gõ thêm một câu trước khi ngồi rà thì bảng vẫn
    // nằm nguyên trên màn hình. Đây cũng là ca mà frame done của lượt sau KHÔNG chở bảng (lượt bày bảng
    // hỏng ⇒ fail-open ⇒ lượt chạy như chat thường), tức đúng khe mà một phép xét "lượt này có bảng không"
    // sẽ để lọt.
    [Fact]
    public void Select_StillCatchesATableFromAnEarlierTurn()
    {
        var project = ProjectWith(
            BaTurn(1, t => t.FlowMap = OneFlow),
            BaTurn(2, _ => { }));

        Assert.Equal(PendingConfirmTableGate.FlowMap, PendingConfirmTableGate.Select(project));
    }

    // BẢNG CỘT không bao giờ chặn: BA chốt và hệ thống lưu ngay ở lượt đọc file, người dùng chỉ mở ra sửa
    // khi muốn. Kể cả dự án cũ còn một lượt mang bảng mà file chưa có bảng nào — chặn ở đây là đòi người
    // dùng gửi một cái bảng mà màn hình không còn nút gửi.
    [Fact]
    public void Select_ColumnTable_NeverBlocks()
    {
        const string columns = """
            [{"fileName":"jd.xlsx","column":"Position","meaning":"tên vị trí","used":true}]
            """;

        var project = ProjectWith(BaTurn(1, t => t.ColumnMap = columns));
        project.SourceFiles.Add(new ProjectSourceFile { FileName = "jd.xlsx", Kind = SourceFileKind.Spreadsheet });
        Assert.Null(PendingConfirmTableGate.Select(project));
    }

    // Câu chặn được dựng ở HAI đường — server render lúc tải trang, requirements.js dựng lại ở frame done —
    // và người dùng rà một câu rồi F5 mà thấy câu khác thì không biết bên nào mới là thật. Test này giữ hai
    // bản khớp nhau từng chữ: đổi câu ở C# mà quên JS (hoặc ngược lại) là fail ngay tại build.
    [Fact]
    public void GateHint_MatchesTheTemplateRequirementsJsBuilds()
    {
        var js = File.ReadAllText(FindRepoFile(Path.Combine("wwwroot", "js", "requirements.js")));
        var start = js.IndexOf("function tableGateHint(", StringComparison.Ordinal);
        Assert.True(start > 0, "requirements.js không còn hàm tableGateHint — cổng chặn mất câu chữ phía client.");

        // Thân hàm chỉ có hai literal backtick nối nhau; gom chúng lại là ra đúng câu JS phát ra.
        var body = js[start..js.IndexOf("\n    }", start, StringComparison.Ordinal)];
        var fromJs = string.Concat(System.Text.RegularExpressions.Regex
            .Matches(body, "`([^`]*)`")
            .Select(m => m.Groups[1].Value));

        // Bản C# của cùng câu đó, thay hai chỗ chở dữ liệu bằng đúng hai biến của JS. Thay nhãn nút TRƯỚC:
        // "Gửi bảng luồng" có chứa "bảng luồng".
        var fromCsharp = PendingConfirmTableGate.FlowMap.GateHint
            .Replace(PendingConfirmTableGate.FlowMap.SendLabel, "${send}", StringComparison.Ordinal)
            .Replace(PendingConfirmTableGate.FlowMap.Name, "${name}", StringComparison.Ordinal);

        Assert.Equal(fromCsharp, fromJs);
    }

    // Bong bóng #tableGate chỉ được NÓI khi cổng lẽ ra đã mở — nếu không, nó lặp lại đúng cái việc mà bảng
    // ngay phía trên (đã có nút gửi và câu dẫn của chính nó) vừa nói, và giải thích vì sao thiếu một cái nút
    // chưa ai hứa. Luật này sống ở HAI chỗ vẽ ra cổng, nên test giữ cả hai cùng có nó: sửa một bên rồi quên
    // bên kia thì rà xong một màn hình rồi F5 lại thấy câu trả lời khác.
    [Fact]
    public void TableGateBubble_SpeaksOnlyWhenTheGateWouldHaveOpened_OnBothSides()
    {
        // Phía server: luật sống ở WriteRequirementGate.Resolve, view chỉ đọc kết quả.
        var view = File.ReadAllText(FindRepoFile(Path.Combine("Views", "Requirements", "Index.cshtml")));
        Assert.Contains("var tableGateSpeaks = writeReqGate.TableGateSpeaks;", view);
        Assert.Contains("@(tableGateSpeaks ? \"\" : \"hidden\")", view);

        var t0 = new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc);
        var invited = new[] { new AgentConversation { Role = "assistant", ReadinessVerified = true, CreatedAt = t0 } };
        var justShowedTheTable = new[] { new AgentConversation { Role = "assistant", Message = "Anh/chị rà bảng luồng giúp mình.", CreatedAt = t0 } };

        var failOpen = WriteRequirementGate.Resolve(invited, Array.Empty<WorkflowRun>(), briefExists: false, tablePending: true);
        Assert.Equal(WriteRequirementGate.Table, failOpen.Value);
        Assert.True(failOpen.TableGateSpeaks);

        var quiet = WriteRequirementGate.Resolve(justShowedTheTable, Array.Empty<WorkflowRun>(), briefExists: false, tablePending: true);
        Assert.Equal(WriteRequirementGate.Table, quiet.Value);
        Assert.False(quiet.TableGateSpeaks);

        var js = File.ReadAllText(FindRepoFile(Path.Combine("wwwroot", "js", "requirements.js")));
        var start = js.IndexOf("const tableGate = document.getElementById(\"tableGate\")", StringComparison.Ordinal);
        Assert.True(start > 0, "requirements.js không còn nhánh dựng #tableGate.");
        Assert.Contains("writeReqWouldOpen", js[start..js.IndexOf("\n        }", start, StringComparison.Ordinal)]);
    }

    // wwwroot/ không được copy sang bin của test (chỉ Prompts/ có), nên đi ngược từ thư mục chạy lên tới
    // repo root — cùng cách CoveragePromptFixture dò thư mục Prompts.
    private static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Không tìm thấy " + relativePath + " từ " + AppContext.BaseDirectory);
    }

    // FAIL-OPEN, cùng luật với mọi cổng khác ở đây: chặn nhầm một vòng soạn hợp lệ đắt hơn nhiều so với để
    // lọt một vòng, nên hội thoại chưa nạp (caller nào đó quên Include) là "không có bảng nào chờ".
    [Fact]
    public void Select_ReturnsNull_WhenTheConversationIsNotLoaded()
    {
        Assert.Null(PendingConfirmTableGate.Select(new Project { Name = "JD Library" }));
    }
}
