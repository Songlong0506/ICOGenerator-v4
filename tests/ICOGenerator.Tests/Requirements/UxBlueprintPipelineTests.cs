using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Evals;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Đường đi của bản thiết kế UX ra khỏi lượt thiết kế: chuẩn hoá đề xuất của model, chèn tất định vào AI Design
// Spec (§5 menu, §6c hành động máy đọc được, §6d hành trình), PocSpec đọc lại §6c, audit POC kiểm từng hành
// động, đối chiếu spec ↔ bản thiết kế, và bộ chấm tất định của Prompt Evals. Mỗi chỗ là một mối nối — hỏng
// một chỗ là thiết kế "đúng trên giấy" mà bản demo không theo, và không cổng nào thấy.
public class UxBlueprintPipelineTests
{
    private static readonly UxBlueprint Inventory = new()
    {
        Objects = { new UxObject { Name = "Locker", States = { "Available", "Assigned", "Need Clean" } } },
        Tasks =
        {
            new UxTask { Id = "T-01", Name = "Xem danh sách tủ", Object = "Locker", Kind = UxTaskKind.Browse, Actors = { "FCM" } },
            new UxTask { Id = "T-02", Name = "Thu hồi tủ", Object = "Locker", Kind = UxTaskKind.Transition, Actors = { "FCM" }, FromState = "Assigned", ToState = "Need Clean" }
        }
    };

    private static UxBlueprint Designed()
    {
        var bp = UxBlueprintBuilder.FromDraft(new UxBlueprintDraft
        {
            Screens =
            {
                new UxScreenDraft
                {
                    Name = "Màn hình Locker List", Pattern = "List_Detail", Object = "locker", Audience = { "FCM", "Vai lạ" },
                    Actions =
                    {
                        new UxAction { Task = "T-01", Label = "Tìm tủ", Placement = "filter" },
                        new UxAction { Task = "T-02 Thu hồi tủ", Label = "Thu hồi tủ", Placement = "dòng", Prominence = "PRIMARY" },
                        new UxAction { Task = "T-99", Label = "Tác vụ bịa" }
                    }
                }
            },
            Navigation = { new UxNavigation { Role = "FCM", Landing = "Locker List", Items = { new UxNavItem { Label = "Locker List", Screen = "Locker List" } } } }
        }, Inventory, new[] { "FCM" });
        UxBlueprintBuilder.Repair(bp, new[] { "FCM" });
        return bp;
    }

    // ── chuẩn hoá ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FromDraft_KeepsOnlyRealTasks_CleansNames_AndDefaultsWhatTheModelGotWrong()
    {
        var bp = Designed();
        var screen = Assert.Single(bp.Screens);

        Assert.Equal("Locker List", screen.Name);             // chữ dẫn "Màn hình" bị bỏ
        Assert.Equal(UxPattern.ListDetail, screen.Pattern);   // "List_Detail" nhận ra được
        Assert.Equal("Locker", screen.Object);                // chữ của danh mục đối tượng
        Assert.Equal(new[] { "FCM" }, screen.Audience);       // vai lạ bị bỏ
        Assert.Equal(new[] { "T-01", "T-02" }, screen.Actions.Select(a => a.Task)); // T-99 bị bỏ, "T-02 Thu hồi tủ" nhận ra

        var recall = screen.Actions.Single(a => a.Task == "T-02");
        Assert.Equal(UxPlacement.Row, recall.Placement);      // vị trí lạ ⇒ mặc định theo loại tác vụ
        Assert.Equal(UxProminence.Primary, recall.Prominence);
        Assert.Equal("Assigned", recall.WhenState);           // nút chuyển trạng thái hiện theo trạng thái nguồn
        Assert.Equal("S-01", screen.Id);
    }

    // Thiết kế lại giữ ID màn theo tên; màn mới nhận số MỚI, kể cả khi một màn cũ đã bị bỏ.
    [Fact]
    public void FromDraft_KeepsScreenIdsAcrossRedesigns_AndNeverReusesThem()
    {
        var previous = new UxBlueprint
        {
            LastScreenNumber = 4,
            Screens = { new UxScreen { Id = "S-04", Name = "Locker List" } }
        };
        var bp = UxBlueprintBuilder.FromDraft(new UxBlueprintDraft
        {
            Screens =
            {
                new UxScreenDraft { Name = "Locker List", Actions = { new UxAction { Task = "T-01" } } },
                new UxScreenDraft { Name = "Locker Board", Actions = { new UxAction { Task = "T-02" } } }
            }
        }, Inventory, new[] { "FCM" }, previous);

        Assert.Equal(new[] { "S-04", "S-05" }, bp.Screens.Select(s => s.Id));
        Assert.Equal(5, bp.LastScreenNumber);
    }

    // ── chèn vào spec + đọc lại ─────────────────────────────────────────────────────────────────────

    private const string Spec = """
        # AI Design Spec
        ## 5. Navigation Structure
        - model tự bịa một menu khác
        ## 6. Screens To Generate
        ### 6.1. Locker List
        - bảng tủ
        ## 6b. Permission Matrix
        - PM-1 (Locker List): Thu hồi tủ — FCM (tất cả)
        ## 7. UI/UX Direction
        - sidebar trái
        """;

    [Fact]
    public void SpecSections_ReplaceTheNavigation_AndInsertTheMachineReadableBlueprint_Idempotently()
    {
        var bp = Designed();
        bp.Journeys = UxJourneyAnalyzer.Journeys(bp, null);
        var json = UxBlueprintBuilder.Serialize(bp);

        var once = UxSpecSections.Apply(Spec, json);
        var twice = UxSpecSections.Apply(once, json);

        Assert.DoesNotContain("model tự bịa", once);
        Assert.Contains("- Vai «FCM» — mở đầu ở: Locker List", once);
        Assert.Contains("- Action T-02 | row | primary | when: Assigned | form: none | Thu hồi tủ", once);
        Assert.True(once.IndexOf(UxSpecSections.BlueprintHeading, StringComparison.Ordinal) < once.IndexOf("## 7. UI/UX Direction", StringComparison.Ordinal));
        Assert.Equal(once, twice); // chạy lại (sinh spec lần hai) không nhân đôi mục nào

        var parsed = PocSpec.Parse(once);
        Assert.Equal(new[] { "Locker List" }, parsed.Screens);   // §6c KHÔNG bị đếm thành màn thứ hai
        Assert.Contains(parsed.Actions, a => a.Screen == "Locker List" && a.TaskId == "T-02" && a.Placement == "row" && a.Label == "Thu hồi tủ");
    }

    [Fact]
    public void SpecSections_LeaveTheSpecAlone_WithoutABlueprint()
        => Assert.Equal(Spec, UxSpecSections.Apply(Spec, null));

    // ── audit POC kiểm từng hành động ───────────────────────────────────────────────────────────────

    [Fact]
    public void PocAudit_ReportsABlueprintActionMissingFromTheDemo()
    {
        var spec = PocSpec.Parse(UxSpecSections.Apply(Spec, UxBlueprintBuilder.Serialize(Designed())));
        var html = PocDoc("""<section class="page-view" data-view="Locker List"><input placeholder="Tìm tủ"> Tìm tủ</section>""");

        var outcome = PocAudit.RunDetailed(html, spec);

        Assert.Contains(outcome.Issues, i => i.Contains("\"Thu hồi tủ\" (T-02)", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Issues, i => i.Contains("\"Tìm tủ\"", StringComparison.Ordinal));
    }

    // Nút trên dòng do JS dựng từ template string: nhãn nằm trong script là đã có.
    [Fact]
    public void PocAudit_AcceptsAnActionRenderedByTheScript()
    {
        var spec = PocSpec.Parse(UxSpecSections.Apply(Spec, UxBlueprintBuilder.Serialize(Designed())));
        var html = PocDoc("""<section class="page-view" data-view="Locker List">Tìm tủ<table></table></section>""",
            "function rows(){ return '<button>Thu hồi tủ</button>'; }");

        Assert.DoesNotContain(PocAudit.RunDetailed(html, spec).Issues, i => i.Contains("UX blueprint action", StringComparison.Ordinal));
    }

    // ── đối chiếu spec ↔ bản thiết kế ───────────────────────────────────────────────────────────────

    [Fact]
    public void ParityChecker_FlagsMissingAndExtraScreensAgainstTheBlueprint()
    {
        var json = UxBlueprintBuilder.Serialize(Designed());
        var spec = Spec.Replace("### 6.1. Locker List", "### 6.1. Locker Recovery");

        var report = SpecBriefParityChecker.CheckBlueprint(json, spec)!;

        Assert.Contains("- Locker List", report);
        Assert.Contains("- Locker Recovery", report);
        Assert.Null(SpecBriefParityChecker.CheckBlueprint(json, Spec));
    }

    // ── Prompt Evals: chấm tất định ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EvalScorer_ScoresAModelOutputOnTheGoldenInput()
    {
        var golden = UxGoldenCases.Locker();
        const string output = """
            {"screens":[{"name":"Locker List","pattern":"list","object":"Locker","audience":["FCM"],"actions":[{"task":"T-01","label":"Tìm tủ","placement":"filter"}]}],
             "navigation":[{"role":"FCM","landing":"Locker List","items":[{"label":"Locker List","screen":"Locker List"}]}]}
            """;

        var result = UxBlueprintEvalScorer.Score(UxGoldenCases.Render(golden), output);

        Assert.NotNull(result);
        Assert.True(result!.Metrics.UnplacedTasks > 0);
        Assert.Contains(result.Findings, f => f.Code == UxLintCode.UnplacedTask);
        Assert.Contains("## Chỉ số tất định", UxBlueprintEvalScorer.Render(result));
        Assert.Contains("không đọc được", UxBlueprintEvalScorer.Render(UxBlueprintEvalScorer.Score("không có khối JSON", output)));
    }

    [Fact]
    public void GoldenScenarios_AreSeededForTheBlueprintPrompt()
    {
        var scenarios = EvalScenariosSeedData.Build().Where(s => UxBlueprintEvalScorer.Applies(s.PromptKey)).ToList();

        Assert.Equal(UxGoldenCases.All().Count, scenarios.Count);
        Assert.All(scenarios, s => Assert.NotNull(UxDesignInputBuilder.TryParse(s.UserInput)));
    }

    // ── prompt biết mọi luật hệ thống sẽ kiểm ──────────────────────────────────────────────────────

    // Mỗi mã lỗi lint phải được kể trong prompt thiết kế: vòng phản biện gửi lại đúng mã đó, và model chỉ sửa
    // trúng khi nó đã biết luật từ đầu.
    [Fact]
    public void BlueprintPrompt_NamesEveryErrorRuleTheLintEnforces()
    {
        var prompt = PromptFixture.Read(UxDesignService.BlueprintPromptKey);
        var errorCodes = new[]
        {
            UxLintCode.UnplacedTask, UxLintCode.EmptyScreen, UxLintCode.TransitionScreen, UxLintCode.DuplicateHome,
            UxLintCode.FormShouldBeModal, UxLintCode.FragmentedFlow, UxLintCode.CatalogSprawl, UxLintCode.PrimaryOverload,
            UxLintCode.ReportMisplaced, UxLintCode.Unreachable, UxLintCode.NoNavigation
        };

        Assert.All(errorCodes, code => Assert.Contains($"`{code}`", prompt, StringComparison.Ordinal));
    }

    private static string PocDoc(string content, string script = "var state = 1;")
    {
        var doc =
            "<html><body>\n<aside class=\"sidebar\">\n<nav class=\"sidebar-nav\">\n" +
            "<div class=\"nav-item\" title=\"Locker List\"><span class=\"nav-label\">Locker List</span></div>\n" +
            "</nav>\n</aside>\n" +
            PocTemplate.StartMarker + "\n" + content + "\n" + PocTemplate.EndMarker + "\n" +
            "    " + PocTemplate.ScriptStartMarker + "\n    <script>\n    " + PocTemplate.ScriptPlaceholder + "\n    </script>\n    " + PocTemplate.ScriptEndMarker + "\n" +
            "</body></html>";
        return PocTemplate.ReplaceScript(doc, script)!;
    }
}
