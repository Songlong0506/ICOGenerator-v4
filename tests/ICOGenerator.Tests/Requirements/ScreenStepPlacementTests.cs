using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// BƯỚC LUỒNG KHÔNG AI PHỤ TRÁCH LÀ VIỆC CỦA BA, KHÔNG PHẢI CÂU HỎI CHO NGƯỜI DÙNG.
//
// Ca thật (dự án JD Library 2): người dùng tự tay rà và chốt bảng luồng, trong đó bước 4 của luồng chính là
// "Xem danh sách nhân viên trực tiếp dưới quyền". Mấy lượt sau BA bày ra bảng màn hình mười bảy dòng —
// JD Library, JD Assignment, JD Detail, MyJD, sáu màn danh mục, bốn màn báo cáo — và không chức năng nào
// trong số đó nhận bước ấy. Phép kiểm tất định bắt đúng, nhưng thứ hiện ra dưới bảng là một câu hỏi:
// "Chưa chức năng nào phụ trách các bước: Xem danh sách nhân viên trực tiếp dưới quyền. Anh/chị điền bước
// đó vào ô bên phải của chức năng phù hợp, hoặc nhắn cho mình biết nếu thiếu hẳn một màn hình."
//
// Câu đó đòi người dùng nghiệp vụ làm hai việc của BA — ánh xạ một bước sang một chức năng trên một màn
// hình, và nhận ra khi cả phạm vi màn hình còn thiếu một chỗ — ngay sau khi họ vừa rà xong mười bảy dòng.
// Mà dữ kiện để trả lời thì BA có đủ: bảng luồng nói ai làm bước đó để làm gì, bảng màn hình nói đang có
// những chỗ nào, và JD Assignment ("Gán JD cho từng nhân viên") nằm ngay trên bảng.
//
// Phân vai sau khi sửa: code vẫn quyết định CÓ lỗ hổng hay không (UncoveredActions) và lời xếp chỗ nào
// được nhận (ApplyPlacements); model chỉ trả lời câu ngữ nghĩa "bước này là việc của chức năng nào"; và
// kết quả ra bảng ở dạng TÍCH SẴN để người dùng vẫn là người chốt.
public class ScreenStepPlacementTests
{
    private const string StepViewStaff = "Xem danh sách nhân viên trực tiếp dưới quyền";
    private const string StepAssign = "Gán JD tương ứng cho từng nhân viên";

    // Đúng bảng luồng người dùng đã chốt ở ca thật, rút còn hai bước liên quan.
    private const string ConfirmedFlow = """
        [{"name":"Tạo, duyệt và gán JD","kind":"luồng chính","role":"Manager orgUnit","steps":[
            {"actor":"Manager orgUnit","action":"Xem danh sách nhân viên trực tiếp dưới quyền","outcome":"","included":true},
            {"actor":"Manager orgUnit","action":"Gán JD tương ứng cho từng nhân viên","outcome":"JD được gán cho nhân viên","included":true}]}]
        """;

    private static List<ScreenScopeRow> TableAsShown() => new()
    {
        new ScreenScopeRow
        {
            Screen = "JD Library",
            Purpose = "Tra cứu và quản lý danh sách JD trong ứng dụng.",
            Functions = new List<ScreenFunction>
            {
                new() { Name = "Xem danh sách JD" }
            }
        },
        new ScreenScopeRow
        {
            Screen = "JD Assignment",
            Purpose = "Gán JD cho từng nhân viên và theo dõi thông tin assignment.",
            Functions = new List<ScreenFunction>
            {
                new() { Name = "Xem danh sách assignment" },
                new() { Name = "Tạo assignment", FlowSteps = new List<string> { StepAssign } }
            }
        }
    };

    // LỖI GỐC, đo ở đúng chỗ nó lộ ra: bảng như BA bày ra để một bước người dùng đã chốt không ai làm.
    [Fact]
    public void UncoveredActions_FindsTheOrphanStep_InTheTableAsTheBAFirstBuiltIt()
    {
        var uncovered = ScreenScopeMapBuilder.UncoveredActions(TableAsShown(), ConfirmedFlow);

        Assert.Equal(new[] { StepViewStaff }, uncovered);
    }

    // CA THƯỜNG GẶP NHẤT: màn hình đúng đã có, chỉ chưa có chức năng nào làm việc đó. BA thêm chức năng vào
    // đúng màn ấy, và bảng hiện ra đã KÍN — không còn gì để hỏi ngược người dùng.
    [Fact]
    public void ApplyPlacements_AddsTheMissingFunction_ToAnExistingScreen()
    {
        var rows = ScreenScopeMapBuilder.ApplyPlacements(
            TableAsShown(),
            new[]
            {
                new ScreenStepPlacement
                {
                    Step = StepViewStaff,
                    Screen = "JD Assignment",
                    Function = "Xem danh sách nhân viên dưới quyền"
                }
            },
            new[] { StepViewStaff });

        var assignment = rows.Single(r => r.Screen == "JD Assignment");
        var added = assignment.Functions.Single(f => f.Name == "Xem danh sách nhân viên dưới quyền");
        Assert.Equal(new[] { StepViewStaff }, added.FlowSteps);
        // TÍCH SẴN như mọi đề xuất khác của BA: người dùng vẫn là người bỏ nó đi nếu sai.
        Assert.True(added.Included);
        Assert.Empty(ScreenScopeMapBuilder.UncoveredActions(rows, ConfirmedFlow));
    }

    // Chức năng đúng đã có sẵn, chỉ thiếu ô "phục vụ bước" ⇒ gắn thêm bước, KHÔNG đẻ ra một chức năng
    // trùng nghĩa nằm cạnh chức năng cũ.
    [Fact]
    public void ApplyPlacements_AttachesTheStep_ToAFunctionThatAlreadyExists()
    {
        var before = TableAsShown();
        var rows = ScreenScopeMapBuilder.ApplyPlacements(
            before,
            new[]
            {
                new ScreenStepPlacement
                {
                    Step = StepViewStaff,
                    Screen = "JD Assignment",
                    Function = "Xem danh sách assignment"
                }
            },
            new[] { StepViewStaff });

        var assignment = rows.Single(r => r.Screen == "JD Assignment");
        Assert.Equal(2, assignment.Functions.Count);
        Assert.Equal(new[] { StepViewStaff }, assignment.Functions[0].FlowSteps);
    }

    // NGOẠI LỆ CÓ CHỦ Ý của chốt chặn "màn hình bịa": không màn nào đang có làm được bước đã chốt ⇒ dòng
    // mới được sinh ra. Cửa duy nhất trước đây ("nhắn cho mình biết nếu thiếu hẳn một màn hình") đòi người
    // dùng tự nhận ra điều đó trước.
    [Fact]
    public void ApplyPlacements_CreatesANewScreen_WhenNoExistingScreenFits()
    {
        var rows = ScreenScopeMapBuilder.ApplyPlacements(
            TableAsShown(),
            new[]
            {
                new ScreenStepPlacement
                {
                    Step = StepViewStaff,
                    Screen = "Team Roster",
                    Function = "Xem danh sách nhân viên dưới quyền",
                    Purpose = "Manager xem những nhân viên trực tiếp dưới quyền mình."
                }
            },
            new[] { StepViewStaff });

        var added = rows.Single(r => r.Screen == "Team Roster");
        Assert.Equal("Manager xem những nhân viên trực tiếp dưới quyền mình.", added.Purpose);
        Assert.True(added.Included);
        // Đề xuất của BA, không phải dòng người dùng tự gõ: mượn cờ đó là gán chữ ký của họ lên một dòng
        // họ chưa nhìn thấy.
        Assert.False(added.AddedByUser);
        Assert.Empty(ScreenScopeMapBuilder.UncoveredActions(rows, ConfirmedFlow));
    }

    // CHỈ LẤP, KHÔNG SỬA. Lời xếp chỗ không trỏ vào một bước mồ côi thì bị bỏ — nếu không, một lượt sinh ra
    // để vá lỗ hổng thành đường vòng cho model viết lại cả bảng, kể cả phần người dùng đã tự tay rà ở lần
    // chốt trước (bảng này bày LẠI được).
    [Fact]
    public void ApplyPlacements_DropsAPlacement_ForAStepThatWasNotOrphaned()
    {
        var rows = ScreenScopeMapBuilder.ApplyPlacements(
            TableAsShown(),
            new[]
            {
                new ScreenStepPlacement
                {
                    Step = StepAssign,
                    Screen = "JD Library",
                    Function = "Gán JD"
                }
            },
            new[] { StepViewStaff });

        Assert.DoesNotContain(rows.Single(r => r.Screen == "JD Library").Functions, f => f.Name == "Gán JD");
    }

    // CHỈ THÊM, KHÔNG BAO GIỜ BỚT: dòng và chức năng người dùng đã bỏ tích ở lần chốt trước phải nguyên
    // trạng sau lượt xếp chỗ. Bật lại thứ họ vừa tắt là đúng lỗi mà cả bộ bảng này dựng ra để chặn.
    [Fact]
    public void ApplyPlacements_NeverRevivesWhatTheUserSwitchedOff()
    {
        var before = TableAsShown();
        before[0].Included = false;
        before[1].Functions[0].Included = false;

        var rows = ScreenScopeMapBuilder.ApplyPlacements(
            before,
            new[]
            {
                new ScreenStepPlacement
                {
                    Step = StepViewStaff,
                    Screen = "JD Assignment",
                    Function = "Xem danh sách nhân viên dưới quyền"
                }
            },
            new[] { StepViewStaff });

        Assert.False(rows.Single(r => r.Screen == "JD Library").Included);
        Assert.False(rows.Single(r => r.Screen == "JD Assignment").Functions[0].Included);
    }

    // Model không xếp được gì (hoặc lời gọi lỗi — cùng đường fail-open) ⇒ bảng nguyên trạng và dòng nhắc cũ
    // hiện ra như trước. Lượt xếp chỗ chỉ được phép LÀM TỐT HƠN một bảng, không bao giờ được chặn nó.
    [Fact]
    public void ApplyPlacements_LeavesTheTableAlone_WhenNothingCouldBePlaced()
    {
        var rows = ScreenScopeMapBuilder.ApplyPlacements(TableAsShown(), Array.Empty<ScreenStepPlacement>(), new[] { StepViewStaff });

        Assert.Equal(new[] { StepViewStaff }, ScreenScopeMapBuilder.UncoveredActions(rows, ConfirmedFlow));
    }

    // Nhánh "làm ngoài phần mềm" có cùng chốt chặn với nhánh xếp chỗ: chỉ bước ĐANG MỒ CÔI mới được nhận.
    // Không có nó thì lượt vá lỗ hổng thành đường để model gạt một bước đã có chức năng ra khỏi ứng dụng.
    [Fact]
    public void ResolveOutsideSteps_OnlyAcceptsOrphanSteps_AndKeepsTheFlowTableWording()
    {
        var outside = ScreenScopeMapBuilder.ResolveOutsideSteps(
            new[]
            {
                new ScreenStepOutside { Step = "xem danh sách nhân viên trực tiếp dưới quyền của mình", Reason = "xem trên giấy" },
                new ScreenStepOutside { Step = "Gán JD tương ứng cho từng nhân viên", Reason = "làm tay" }
            },
            new[] { StepViewStaff });

        var only = Assert.Single(outside);
        Assert.Equal(StepViewStaff, only.Step);
        Assert.Equal("xem trên giấy", only.Reason);
    }

    // Bước mang cờ ngoài phần mềm rời khỏi phép kiểm mối nối.
    [Fact]
    public void AnOutsideSoftwareStep_LeavesTheCoverageCheck()
    {
        var marked = FlowMapBuilder.MarkOutsideSoftware(
            ConfirmedFlow, new[] { new ScreenStepOutside { Step = StepViewStaff, Reason = "xem trên giấy" } });

        Assert.NotNull(marked);
        Assert.Empty(ScreenScopeMapBuilder.UncoveredActions(TableAsShown(), marked));
        // Ghi lần hai không đổi gì ⇒ null, caller khỏi ghi đè cột.
        Assert.Null(FlowMapBuilder.MarkOutsideSoftware(marked, new[] { new ScreenStepOutside { Step = StepViewStaff } }));
    }

    // Ô "phục vụ bước" nhận chữ của BẢNG LUỒNG, không chữ model vừa gõ lại. Model diễn đạt lại bước là
    // chuyện thường (và phép so chứa-nhau vẫn nhận), nhưng ghi bản diễn đạt ấy vào bảng thì hỏng hai chỗ:
    // UncoveredActions so bảng với chính danh sách bước nên bản khác chữ là một báo động giả chực chờ, và
    // người dùng mất đường đối chiếu với các bước họ vừa tự tay rà ở bảng trước.
    [Fact]
    public void ApplyPlacements_WritesTheFlowTableWording_NotTheModelsParaphrase()
    {
        var rows = ScreenScopeMapBuilder.ApplyPlacements(
            TableAsShown(),
            new[]
            {
                new ScreenStepPlacement
                {
                    Step = "xem danh sách nhân viên trực tiếp dưới quyền của mình",
                    Screen = "JD Assignment",
                    Function = "Xem danh sách nhân viên dưới quyền"
                }
            },
            new[] { StepViewStaff });

        var added = rows.Single(r => r.Screen == "JD Assignment")
            .Functions.Single(f => f.Name == "Xem danh sách nhân viên dưới quyền");
        Assert.Equal(new[] { StepViewStaff }, added.FlowSteps);
    }

    // Ba nhánh xếp chỗ phải còn nguyên trong prompt. Nhánh thứ ba — ĐẶT MỘT MÀN HÌNH MỚI — là nhánh dễ bị
    // gọt đi nhất về sau ("model không được tự thêm màn hình"), mà bỏ nó là để lại đúng nửa vấn đề: bước
    // không màn nào làm được lại quay về thành câu hỏi *"nhắn cho mình biết nếu thiếu hẳn một màn hình"*,
    // thứ đòi người dùng nghiệp vụ nhận ra một lỗ hổng phạm vi trước khi nói được.
    [Fact]
    public void ThePlacementPrompt_KeepsAllThreeBranches_IncludingCreatingAScreen()
    {
        var prompt = ReadPrompt("BusinessAnalyst/screen-step-placement.v1.md");

        Assert.Contains("chức năng đang làm đúng việc đó rồi", prompt, StringComparison.Ordinal);
        Assert.Contains("một chức năng MỚI", prompt, StringComparison.Ordinal);
        Assert.Contains("một màn hình MỚI", prompt, StringComparison.Ordinal);
        // …và nhánh thứ tư cùng hai cái phanh của nó: bước làm ngoài phần mềm thì KHAI ra chứ không bịa
        // chức năng để lấp, và phân vân thì coi là thuộc ứng dụng — gạt nhầm một bước ra ngoài là ứng dụng
        // thiếu hẳn chỗ làm việc đó, còn một chức năng thừa thì người dùng chỉ ra được ngay ở bản demo.
        Assert.Contains("`outsideSoftware`", prompt, StringComparison.Ordinal);
        Assert.Contains("KHÔNG bịa chức năng để lấp cho đủ", prompt, StringComparison.Ordinal);
        Assert.Contains("Khi phân vân thì coi là thuộc ứng dụng", prompt, StringComparison.Ordinal);
        // Câu trả bước về cho người dùng không được mọc lại.
        Assert.DoesNotContain("hệ thống sẽ hỏi người dùng", prompt, StringComparison.Ordinal);
    }

    // Cùng cách tìm Prompts/ như SupersededRuleTests: ưu tiên bản copy trong bin, không có thì đi ngược lên
    // repo root.
    private static string ReadPrompt(string promptKey)
    {
        var relative = promptKey.Replace('/', Path.DirectorySeparatorChar);

        var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts", relative);
        if (File.Exists(fromBin))
            return File.ReadAllText(fromBin);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Prompts", relative);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("Không tìm thấy prompt " + promptKey + " từ " + AppContext.BaseDirectory);
    }
}
