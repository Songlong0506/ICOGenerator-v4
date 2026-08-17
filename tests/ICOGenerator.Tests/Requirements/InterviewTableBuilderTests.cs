using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Ba builder của ba bảng chốt còn lại. Chúng là CHỐT CHẶN TẤT ĐỊNH cùng họ với PermissionMatrixBuilder và
// SourceColumnMapBuilder, nên test ở đây nhắm đúng các đường hỏng đã biết chứ không nhắm độ phủ:
//
//  • cờ tích/bỏ tích ở lượt BÀY BẢNG phải luôn là TÍCH SẴN bất kể model trả gì (structured output buộc
//    điền đủ trường, nên một model điền false cho có sẽ âm thầm bỏ tích sạch bảng);
//  • LUẬT BẰNG CHỨNG: không có trích dẫn thì không khóa được ô nào;
//  • dòng bịa bị loại, dòng bị bỏ quên vẫn phải có mặt;
//  • phép kiểm mối nối luồng ⇄ màn hình.
public class InterviewTableBuilderTests
{
    // ==== BẢNG LUỒNG ====

    [Fact]
    public void FlowMap_TicksEveryStepOnTheProposalPath()
    {
        var rows = FlowMapBuilder.Build(new[]
        {
            new FlowMapRow
            {
                Name = "Đăng ký khóa học",
                Steps = new List<FlowMapStep>
                {
                    new() { Actor = "Nhân viên", Action = "Gửi đơn", Included = false },
                    new() { Actor = "Quản lý", Action = "Duyệt đơn", Included = false }
                }
            }
        });

        Assert.All(rows.Single().Steps, s => Assert.True(s.Included));
    }

    [Fact]
    public void FlowMap_KeepsTheUserSelectionOnTheSubmitPath()
    {
        var rows = FlowMapBuilder.Sanitize(new[]
        {
            new FlowMapRow
            {
                Name = "Đăng ký khóa học",
                Steps = new List<FlowMapStep>
                {
                    new() { Action = "Gửi đơn", Included = true },
                    new() { Action = "Bước bịa", Included = false }
                }
            }
        });

        var steps = rows.Single().Steps;
        Assert.True(steps[0].Included);
        Assert.False(steps[1].Included);
    }

    // Cờ suông không khóa được bước nào — cùng luật với PermissionGrant.Locked. Không có ranh giới này thì
    // bảng điền sẵn trông như đã chốt, và người dùng bấm gửi trong ba giây.
    [Fact]
    public void FlowMap_LocksOnlyStepsThatCarryEvidence()
    {
        var rows = FlowMapBuilder.Build(new[]
        {
            new FlowMapRow
            {
                Name = "Đăng ký khóa học",
                Steps = new List<FlowMapStep>
                {
                    new() { Action = "Gửi đơn", Locked = true, Evidence = "" },
                    new() { Action = "Duyệt đơn", Evidence = "quản lý duyệt xong là khóa" }
                }
            }
        });

        var steps = rows.Single().Steps;
        Assert.False(steps[0].Locked);
        Assert.True(steps[1].Locked);
    }

    // Một "luồng" một bước là một câu mô tả, không phải luồng: nó không kiểm được bằng oracle và cũng
    // không cho người dùng chỗ nào để bắt lỗi thứ tự.
    [Fact]
    public void FlowMap_DropsSingleStepFlows()
    {
        var rows = FlowMapBuilder.Build(new[]
        {
            new FlowMapRow { Name = "Xem báo cáo", Steps = new List<FlowMapStep> { new() { Action = "Mở trang" } } }
        });

        Assert.Empty(rows);
    }

    // Bước thiếu vai đọc lên như một sự kiện tự xảy ra, và phần "ai làm bước nào" — thứ quyết định cả
    // phân quyền lẫn thông báo ở các bảng sau — biến mất mà không ai nhìn thấy để hỏi.
    [Fact]
    public void FlowMap_FallsBackToTheFlowRoleForStepsWithoutAnActor()
    {
        var rows = FlowMapBuilder.Build(new[]
        {
            new FlowMapRow
            {
                Name = "Đăng ký khóa học",
                Role = "Nhân viên",
                Steps = new List<FlowMapStep> { new() { Action = "Gửi đơn" }, new() { Action = "Chờ duyệt" } }
            }
        });

        Assert.All(rows.Single().Steps, s => Assert.Equal("Nhân viên", s.Actor));
    }

    // Ngoại lệ là phần khó lấy nhất của cả buổi phỏng vấn, mà theo prompt nó nằm SAU luồng chính — cắt
    // tuần tự ở trần sẽ luôn vứt đúng nó đi đầu tiên.
    [Fact]
    public void FlowMap_NeverDropsExceptionsToFitTheCap()
    {
        var proposed = Enumerable.Range(1, FlowMapBuilder.MaxFlows + 3)
            .Select(i => new FlowMapRow
            {
                Name = $"Luồng chính {i}",
                Steps = new List<FlowMapStep> { new() { Action = "Bước 1" }, new() { Action = "Bước 2" } }
            })
            .Append(new FlowMapRow
            {
                Name = "Bị từ chối",
                Kind = "ngoại lệ",
                Steps = new List<FlowMapStep> { new() { Action = "Từ chối" }, new() { Action = "Sửa lại" } }
            })
            .ToList();

        var rows = FlowMapBuilder.Build(proposed);

        Assert.Contains(rows, r => r.Kind == FlowKind.Exception);
        Assert.True(rows.Count <= FlowMapBuilder.MaxFlows);
    }

    // Bước bị loại phải được NÓI RA trong tin nhắn gửi vào hội thoại: im lặng bỏ đi thì người dùng không
    // có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại.
    [Fact]
    public void FlowMap_UserMessageNamesTheDroppedSteps()
    {
        var rows = FlowMapBuilder.Sanitize(new[]
        {
            new FlowMapRow
            {
                Name = "Đăng ký khóa học",
                Steps = new List<FlowMapStep>
                {
                    new() { Action = "Gửi đơn", Included = true },
                    new() { Action = "Kế toán ghi sổ", Included = false }
                }
            }
        });

        Assert.Contains("Kế toán ghi sổ", FlowMapBuilder.RenderUserMessage(rows));
    }

    // ==== BẢNG MÀN HÌNH ====

    private static readonly List<string> Scope = new() { "Màn hình Training Plan", "Trang duyệt của HOD" };

    // Màn hình model bỏ quên vẫn phải có mặt, và phải TÍCH SẴN: đưa vào ở trạng thái bỏ tích là ra quyết
    // định loại thay người dùng ở đúng chỗ họ không nhìn thấy để phản đối.
    [Fact]
    public void ScreenScope_AddsForgottenScreensTicked()
    {
        var rows = ScreenScopeMapBuilder.Build(
            new[] { new ScreenScopeRow { Screen = "Màn hình Training Plan", Purpose = "Lập kế hoạch" } }, Scope);

        Assert.Equal(2, rows.Count);
        var forgotten = rows.Single(r => r.Screen == "Trang duyệt của HOD");
        Assert.True(forgotten.Included);
    }

    // Một dòng bịa lọt qua là một tính năng ngoài phạm vi đi vào tài liệu mang chữ ký người dùng.
    [Fact]
    public void ScreenScope_DropsScreensOutsideThePlannedScope()
    {
        var rows = ScreenScopeMapBuilder.Build(
            new[] { new ScreenScopeRow { Screen = "Màn hình quản trị hệ thống" } }, Scope);

        Assert.DoesNotContain(rows, r => r.Screen == "Màn hình quản trị hệ thống");
    }

    // Cờ "người dùng tự thêm" chỉ có nghĩa ở đường GỬI. Ở lượt BÀY BẢNG mọi dòng đều do model soạn, nên đọc
    // cờ ở đây là dựng cho model một cửa sau đi vòng chốt chặn "màn hình bịa" bằng cách tự khai là người
    // dùng — mà đó đúng là thứ chốt chặn này sinh ra để chặn.
    [Fact]
    public void ScreenScope_IgnoresTheUserAddedFlagOnTheProposalPath()
    {
        var rows = ScreenScopeMapBuilder.Build(
            new[] { new ScreenScopeRow { Screen = "Màn hình quản trị hệ thống", AddedByUser = true } }, Scope);

        Assert.DoesNotContain(rows, r => r.Screen == "Màn hình quản trị hệ thống");
    }

    // Đường GỬI thì ngược lại: người dùng là người có thẩm quyền về phạm vi. Dòng họ tự gõ đi qua được, và
    // giữ cờ để tin nhắn kể lại còn gọi tên được nó.
    [Fact]
    public void ScreenScope_AcceptsScreensTheUserAddedOnTheSubmitPath()
    {
        var rows = ScreenScopeMapBuilder.Sanitize(new[]
        {
            new ScreenScopeRow { Screen = "Màn hình Training Plan", Included = true },
            new ScreenScopeRow { Screen = "Trang duyệt của HOD", Included = true },
            new ScreenScopeRow { Screen = "Báo cáo tổng hợp cuối năm", AddedByUser = true, Included = true }
        }, Scope);

        var added = rows.Last();
        Assert.Equal("Báo cáo tổng hợp cuối năm", added.Screen);
        Assert.True(added.AddedByUser);
        Assert.Contains("Các màn hình mình tự bổ sung vào bảng: Báo cáo tổng hợp cuối năm.",
            ScreenScopeMapBuilder.RenderUserMessage(rows));
    }

    // Cờ `included` là chỗ NGƯỜI DÙNG loại một chức năng, không phải chỗ model tự phủ nhận đề xuất của
    // mình. Structured output buộc điền đủ trường, nên một model điền false cho có sẽ bày ra một bảng bỏ
    // tích sạch và người dùng gửi đi một phạm vi rỗng trong khi tưởng vừa xác nhận cả ứng dụng.
    [Fact]
    public void ScreenScope_TicksEveryFunctionOnTheProposalPath()
    {
        var rows = ScreenScopeMapBuilder.Build(new[]
        {
            new ScreenScopeRow
            {
                Screen = "Màn hình Training Plan",
                Functions = new List<ScreenFunction>
                {
                    new() { Name = "Xem danh sách", Included = false },
                    new() { Name = "Tạo version plan", Included = false }
                }
            }
        }, Scope);

        Assert.All(rows.Single(r => r.Screen == "Màn hình Training Plan").Functions, f => Assert.True(f.Included));
    }

    // Đường GỬI thì ngược lại: bỏ tích là quyết định của người dùng và phải được giữ nguyên. Dòng chức năng
    // TRỐNG (chỗ "thêm chức năng…" của giao diện) không có tên nên không phải một chức năng — bỏ đi.
    [Fact]
    public void ScreenScope_KeepsFunctionChoicesAndDropsBlankRowsOnTheSubmitPath()
    {
        var rows = ScreenScopeMapBuilder.Sanitize(new[]
        {
            new ScreenScopeRow
            {
                Screen = "Màn hình Training Plan",
                Included = true,
                Functions = new List<ScreenFunction>
                {
                    new() { Name = "Xem danh sách", Included = true },
                    new() { Name = "Xóa version plan", Included = false },
                    new() { Name = "   ", Included = true }
                }
            }
        }, Scope);

        var functions = rows.Single(r => r.Screen == "Màn hình Training Plan").Functions;
        Assert.Equal(2, functions.Count);
        Assert.True(functions.Single(f => f.Name == "Xem danh sách").Included);
        Assert.False(functions.Single(f => f.Name == "Xóa version plan").Included);
    }

    // GỘP: mục phạm vi thực ra là một chức năng của màn hình khác thì nó nằm ở cột chức năng, KHÔNG mọc
    // thành một dòng riêng. Không có `covers` thì chốt chặn "màn hình bị bỏ quên" bổ sung nó lại ngay bên
    // dưới — và cột "Màn hình" quay về đúng cái mớ lẫn màn hình/tính năng/luồng mà bảng này phải dọn.
    [Fact]
    public void ScreenScope_FoldsScopeItemsAnotherRowClaimedToCover()
    {
        var scope = new List<string>
        {
            "Trang Training Plan Detail",
            "Tính năng Generate Training Implement từ Training Plan Detail"
        };

        var rows = ScreenScopeMapBuilder.Build(new[]
        {
            new ScreenScopeRow
            {
                Screen = "Trang Training Plan Detail",
                Functions = new List<ScreenFunction> { new() { Name = "Generate Training Implement" } },
                Covers = new List<string> { "Tính năng Generate Training Implement từ Training Plan Detail" }
            }
        }, scope);

        Assert.Equal(new[] { "Trang Training Plan Detail" }, rows.Select(r => r.Screen));
        Assert.Equal("Generate Training Implement", Assert.Single(rows[0].Functions).Name);
    }

    // Lời khai gộp KHÔNG được làm biến mất một màn hình có dòng của chính nó: dòng luôn thắng. Nếu không,
    // model chỉ cần khai bừa một tên vào `covers` là cả một màn hình rời khỏi phạm vi trong im lặng.
    [Fact]
    public void ScreenScope_KeepsAScreenThatOwnsItsRow_EvenWhenAnotherRowClaimsToCoverIt()
    {
        var rows = ScreenScopeMapBuilder.Build(new[]
        {
            new ScreenScopeRow { Screen = "Màn hình Training Plan", Covers = new List<string> { "Trang duyệt của HOD" } },
            new ScreenScopeRow { Screen = "Trang duyệt của HOD" }
        }, Scope);

        Assert.Equal(Scope, rows.Select(r => r.Screen));
    }

    // Mục đã gộp không được QUAY LẠI ở lượt sau. Lượt chắt lọc PlannedScope là một lời gọi LLM chạy độc
    // lập với bảng, nên nó vẫn giữ mãi mục cũ; phép lọc này là thứ bảo đảm nó không mọc lại thành một dòng
    // của bảng phân quyền và một màn hình của bản demo.
    [Fact]
    public void ScreenScope_EffectiveScreensDoesNotResurrectCoveredItems()
    {
        const string confirmed = """
            [{"screen":"Trang Training Plan Detail","included":true,
              "covers":["Tính năng Generate Training Implement từ Training Plan Detail"]}]
            """;
        var laterScope = new List<string>
        {
            "Trang Training Plan Detail",
            "Tính năng Generate Training Implement từ Training Plan Detail",
            "Báo cáo tổng hợp"
        };

        var screens = ScreenScopeMapBuilder.EffectiveScreens(confirmed, laterScope);

        Assert.Equal(new[] { "Trang Training Plan Detail", "Báo cáo tổng hợp" }, screens);
    }

    // Phạm vi HIỆU LỰC sau khi bảng chốt: các dòng người dùng GIỮ, cộng mục mới lộ ra sau đó. Mục họ đã
    // bỏ tích không bao giờ quay lại — lượt chắt lọc PlannedScope không đọc bảng nên nó vẫn giữ mục đó
    // mãi, và mở lại thứ họ vừa đóng là đúng lỗi mà bảng cột đã cấm.
    [Fact]
    public void ScreenScope_EffectiveScreensDropsUntickedRowsButKeepsNewOnes()
    {
        var confirmed = """
            [{"screen":"Màn hình Training Plan","included":true},
             {"screen":"Trang duyệt của HOD","included":false}]
            """;
        var laterScope = new List<string> { "Màn hình Training Plan", "Trang duyệt của HOD", "Báo cáo tổng hợp" };

        var screens = ScreenScopeMapBuilder.EffectiveScreens(confirmed, laterScope);

        Assert.Equal(new[] { "Màn hình Training Plan", "Báo cáo tổng hợp" }, screens);
    }

    [Fact]
    public void ScreenScope_EffectiveScreensFallsBackToPlannedScopeWhenNotConfirmed()
    {
        Assert.Equal(Scope, ScreenScopeMapBuilder.EffectiveScreens(null, Scope));
    }

    // Bỏ tích SẠCH bảng là bảng hỏng, không phải "ứng dụng không có màn hình nào". Trả rỗng ở đây khóa
    // chết cả tuyến trong im lặng: cổng phân quyền đòi phạm vi có mục mới mở, mà dòng phân quyền chỉ [RÕ]
    // sau khi bảng đó chốt ⇒ nút "Write Requirement" không bao giờ sáng và không gì trên màn hình nói vì sao.
    [Fact]
    public void ScreenScope_EffectiveScreensFallsBackWhenEveryRowWasUnticked()
    {
        const string allUnticked = """
            [{"screen":"Màn hình Training Plan","included":false},
             {"screen":"Trang duyệt của HOD","included":false}]
            """;

        Assert.Equal(Scope, ScreenScopeMapBuilder.EffectiveScreens(allUnticked, Scope));
    }

    // Cùng đường hỏng, phía bảng luồng: một khối "đã chốt" chỉ gồm hai dòng tiêu đề nói dối cả hai chiều —
    // BA đọc thấy đã chốt nên thôi hỏi lại luồng, còn nội dung thì trống.
    [Fact]
    public void FlowMap_ConfirmedBlockIsNullWhenEveryStepWasUnticked()
    {
        var rows = FlowMapBuilder.Sanitize(new[]
        {
            new FlowMapRow
            {
                Name = "Đăng ký khóa học",
                Steps = new List<FlowMapStep>
                {
                    new() { Action = "Gửi đơn", Included = false },
                    new() { Action = "Duyệt đơn", Included = false }
                }
            }
        });

        Assert.Null(FlowMapBuilder.RenderConfirmedBlock(System.Text.Json.JsonSerializer.Serialize(rows)));
    }

    // PHÉP KIỂM MỐI NỐI: hai bảng đọc riêng đều "đạt", chỗ hỏng nằm ở chỗ nối. Một bước không chức năng nào
    // phụ trách nghĩa là hoặc người dùng không có chỗ nào để làm bước đó, hoặc bước đó không có thật.
    [Fact]
    public void ScreenScope_ReportsFlowStepsNoFunctionCovers()
    {
        const string flowMap = """
            [{"name":"Đăng ký","kind":"luồng chính","steps":[
              {"action":"Gửi đơn đăng ký","included":true},
              {"action":"Duyệt đơn đăng ký","included":true}]}]
            """;
        var rows = new List<ScreenScopeRow>
        {
            new()
            {
                Screen = "Màn hình Training Plan",
                Included = true,
                Functions = new List<ScreenFunction> { Fn("Gửi đơn", "Gửi đơn đăng ký") }
            }
        };

        var uncovered = ScreenScopeMapBuilder.UncoveredActions(rows, flowMap);

        Assert.Equal(new[] { "Duyệt đơn đăng ký" }, uncovered);
    }

    // Vì sao bước phải nằm ở CẤP CHỨC NĂNG: bỏ tích một chức năng là bỏ luôn phần việc nó gánh, nên bước
    // của nó phải lập tức hiện ra là chưa ai làm. Bản cũ gắn bước ở cấp màn hình nên người dùng bỏ đúng
    // chức năng chở bước đó mà cả bảng vẫn báo "đủ".
    [Fact]
    public void ScreenScope_ReportsStepsOfFunctionsTheUserUnticked()
    {
        const string flowMap = """
            [{"name":"Đăng ký","kind":"luồng chính","steps":[
              {"action":"Gửi đơn đăng ký","included":true},
              {"action":"Duyệt đơn đăng ký","included":true}]}]
            """;
        var rows = new List<ScreenScopeRow>
        {
            new()
            {
                Screen = "Màn hình Training Plan",
                Included = true,
                Functions = new List<ScreenFunction>
                {
                    Fn("Gửi đơn", "Gửi đơn đăng ký"),
                    new() { Name = "Duyệt đơn", Included = false, FlowSteps = new List<string> { "Duyệt đơn đăng ký" } }
                }
            }
        };

        Assert.Equal(new[] { "Duyệt đơn đăng ký" }, ScreenScopeMapBuilder.UncoveredActions(rows, flowMap));
    }

    // So khớp bằng CHỨA-NHAU sau chuẩn hoá: người dùng sửa ô "phục vụ bước" bằng lời của họ, và một phép
    // so nguyên văn sẽ báo động giả ở gần như mọi dòng — mà cảnh báo luôn sai thì lần sau không ai đọc.
    [Fact]
    public void ScreenScope_MatchesFlowStepsLoosely()
    {
        const string flowMap = """
            [{"name":"Đăng ký","kind":"luồng chính","steps":[
              {"action":"Gửi đơn đăng ký","included":true},
              {"action":"Duyệt đơn","included":true}]}]
            """;
        var rows = new List<ScreenScopeRow>
        {
            new() { Screen = "A", Included = true, Functions = new List<ScreenFunction> { Fn("Đăng ký", "nhân viên gửi đơn đăng ký khóa học") } },
            new() { Screen = "B", Included = true, Functions = new List<ScreenFunction> { Fn("Duyệt", "duyệt đơn") } }
        };

        Assert.Empty(ScreenScopeMapBuilder.UncoveredActions(rows, flowMap));
    }

    // Bước của luồng người dùng đã BỎ TÍCH không được tính là chưa phủ — nếu không, mọi bước họ vừa loại
    // biến thành một cảnh báo đòi họ dựng màn hình cho nó.
    [Fact]
    public void ScreenScope_IgnoresStepsTheUserRemoved()
    {
        const string flowMap = """
            [{"name":"Đăng ký","kind":"luồng chính","steps":[
              {"action":"Gửi đơn","included":true},
              {"action":"Kế toán ghi sổ","included":false}]}]
            """;
        var rows = new List<ScreenScopeRow>
        {
            new() { Screen = "A", Included = true, Functions = new List<ScreenFunction> { Fn("Gửi đơn", "Gửi đơn") } }
        };

        Assert.Empty(ScreenScopeMapBuilder.UncoveredActions(rows, flowMap));
    }

    // BẢN CŨ ĐỌC LẠI ĐƯỢC. Hồi cột chức năng còn là một ô text, dòng được lưu dạng "Functions": "Xem, Tạo"
    // kèm "FlowSteps" ở cấp màn hình (và ở PascalCase, vì cột DB serialize bằng options mặc định).
    // Deserialize thẳng thì kiểu không khớp ⇒ trả mảng rỗng ⇒ một dự án ĐÃ chốt bảng bỗng bị coi như chưa
    // chốt: cổng bảng phân quyền mở lại và khối "bảng đã chốt" biến khỏi ngữ cảnh, tất cả trong im lặng.
    [Fact]
    public void ScreenScope_ParsesRowsWrittenBeforeFunctionsBecameRows()
    {
        const string legacy = """
            [{"Screen":"Màn hình Training Plan","Purpose":"Lập kế hoạch",
              "Functions":"Xem danh sách, Tạo version plan",
              "FlowSteps":["Tạo một version plan"],"Included":true}]
            """;

        var row = Assert.Single(ScreenScopeMapBuilder.Parse(legacy));

        Assert.Equal("Lập kế hoạch", row.Purpose);
        Assert.Equal(new[] { "Xem danh sách", "Tạo version plan" }, row.Functions.Select(f => f.Name));
        // Bước ở cấp màn hình gắn xuống chức năng đầu tiên: phép kiểm phủ bước so khớp trên cả bảng nên vị
        // trí không đổi kết quả, thứ duy nhất phải giữ là đừng để nó rơi mất.
        Assert.Equal("Tạo một version plan", Assert.Single(row.Functions[0].FlowSteps));
    }

    private static ScreenFunction Fn(string name, params string[] steps)
        => new() { Name = name, Included = true, FlowSteps = steps.ToList() };

    // ==== BẢNG ĐỐI TƯỢNG ====

    [Fact]
    public void EntityMap_TicksEverythingOnTheProposalPath()
    {
        var rows = EntityMapBuilder.Build(new[]
        {
            new EntityMapRow
            {
                Entity = "Kế hoạch đào tạo",
                Included = false,
                Fields = new List<EntityFieldNote> { new() { Name = "Quý", Used = false } }
            }
        });

        var row = rows.Single();
        Assert.True(row.Included);
        Assert.True(row.Fields.Single().Used);
    }

    // Một dòng không có thông tin nào và cũng không có trạng thái nào là một danh từ model nhặt trong hội
    // thoại, không phải đối tượng nghiệp vụ. Bày nó ra là mời người dùng xác nhận một dòng rỗng.
    [Fact]
    public void EntityMap_DropsEntitiesWithNothingInThem()
    {
        Assert.Empty(EntityMapBuilder.Build(new[] { new EntityMapRow { Entity = "Hệ thống" } }));
    }

    // "Vòng đời" một trạng thái không phải vòng đời. Đối tượng vẫn giữ — nó là đối tượng danh mục.
    [Fact]
    public void EntityMap_DropsLifecyclesWithASingleState()
    {
        var rows = EntityMapBuilder.Build(new[]
        {
            new EntityMapRow
            {
                Entity = "Khóa học",
                Fields = new List<EntityFieldNote> { new() { Name = "Tên khóa" } },
                States = new List<EntityLifecycleState> { new() { State = "Đang mở" } }
            }
        });

        Assert.Empty(rows.Single().States);
        Assert.Single(rows);
    }

    // Thông tin trùng một CỘT ĐÃ TÍCH của tài liệu nguồn: người dùng đã trả lời câu đó rồi, chỉ khác là
    // trả lời bằng cách tích. Bắt duyệt lại lần hai là hình dạng vòng lặp câu hỏi chết.
    [Fact]
    public void EntityMap_MarksFieldsAlreadySettledByTheColumnMap()
    {
        var rows = EntityMapBuilder.Build(new[]
        {
            new EntityMapRow
            {
                Entity = "Khóa học",
                Fields = new List<EntityFieldNote> { new() { Name = "Item Title" } },
                States = new List<EntityLifecycleState>()
            }
        }, new[] { "Item Title" });

        Assert.Contains("bảng cột", rows.Single().Fields.Single().Meaning);
    }

    // Người nhận thông báo đã chuyển hẳn sang bảng THÔNG BÁO, nên khối ngữ cảnh của bảng đối tượng không
    // được nhắc tới nó nữa — kể cả với dữ liệu cũ còn mang ô `notify`. Hai khối cùng nói về một quyết định
    // là cách chắc chắn nhất để BA hỏi lại thứ người dùng vừa chốt ở bảng kia.
    [Fact]
    public void EntityMap_ConfirmedBlockNoLongerMentionsRecipients()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            new EntityMapRow
            {
                Entity = "Đơn đăng ký",
                Included = true,
                Fields = new List<EntityFieldNote> { new() { Name = "Người gửi", Used = true } },
                States = new List<EntityLifecycleState>
                {
                    new() { State = "Nháp", EntryCondition = "vừa tạo" },
                    new() { State = "Đã duyệt", EntryCondition = "quản lý duyệt", Notify = "người gửi" }
                }
            }
        });

        var block = EntityMapBuilder.RenderConfirmedBlock(System.Text.Json.JsonSerializer.Serialize(rows));

        Assert.Contains("\"Đã duyệt\" khi quản lý duyệt", block);
        Assert.DoesNotContain("báo cho", block);
    }

    // ==== BẢNG ĐỐI TƯỢNG: dòng NGƯỜI DÙNG TỰ THÊM ====
    // Cùng ranh giới với bảng màn hình: hai chốt chặn "đối tượng rỗng ruột" và "vòng đời một trạng thái"
    // dựng để chặn MODEL, nên chúng phải nhường ở đường GỬI — nhưng chỉ ở đó.

    // Ở lượt BÀY BẢNG mọi dòng đều do model soạn, nên đọc cờ ở đó là dựng cho model một cửa sau đi vòng chốt
    // chặn bằng cách tự khai là người dùng.
    [Fact]
    public void EntityMap_IgnoresTheUserAddedFlagOnTheProposalPath()
    {
        var rows = EntityMapBuilder.Build(new[]
        {
            new EntityMapRow { Entity = "Hệ thống", AddedByUser = true }
        });

        Assert.Empty(rows);
    }

    // Đường GỬI thì ngược lại: họ vừa gõ tên đối tượng vào bảng, tức đã nói "ứng dụng còn phải lưu thứ này".
    // Nuốt nó đi là bắt họ gõ lại vào khung chat đúng thứ vừa gõ trên bảng.
    [Fact]
    public void EntityMap_KeepsAnEmptyEntityTheUserAddedOnTheSubmitPath()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            new EntityMapRow
            {
                Entity = "Kế hoạch đào tạo",
                Included = true,
                Fields = new List<EntityFieldNote> { new() { Name = "Quý", Used = true } }
            },
            new EntityMapRow { Entity = "Hồ sơ giảng viên", Included = true, AddedByUser = true }
        });

        var added = rows.Last();
        Assert.Equal("Hồ sơ giảng viên", added.Entity);
        Assert.True(added.AddedByUser);
        Assert.Contains("Các đối tượng mình tự bổ sung vào bảng: Hồ sơ giảng viên.",
            EntityMapBuilder.RenderUserMessage(rows));
    }

    // Đối tượng rỗng KHÔNG mang cờ tự thêm vẫn bị loại ở đường gửi: cờ là ngoại lệ duy nhất, không phải một
    // cái cửa mở sẵn cho mọi payload.
    [Fact]
    public void EntityMap_StillDropsEmptyEntitiesWithoutTheFlagOnTheSubmitPath()
    {
        Assert.Empty(EntityMapBuilder.Sanitize(new[] { new EntityMapRow { Entity = "Hệ thống", Included = true } }));
    }

    // Khối ngữ cảnh đứng dưới lệnh "đừng hỏi lại", nên đối tượng người dùng tự thêm mà chưa điền gì phải
    // được gọi tên ra là chỗ CẦN hỏi — không nói ra thì nó vĩnh viễn không ai hỏi cần lưu thông tin gì.
    [Fact]
    public void EntityMap_ConfirmedBlockAsksAboutEmptyEntitiesTheUserAdded()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            new EntityMapRow { Entity = "Hồ sơ giảng viên", Included = true, AddedByUser = true }
        });

        var block = EntityMapBuilder.RenderConfirmedBlock(System.Text.Json.JsonSerializer.Serialize(rows));

        Assert.Contains("CHƯA nêu thông tin cần lưu", block);
    }

    // Một trạng thái người dùng tự gõ (hoặc còn lại sau khi họ xóa bớt) là một QUYẾT ĐỊNH. Cắt nó ở đường gửi
    // vừa mất chữ họ vừa gõ, vừa làm cả dòng rơi khỏi bảng theo luật "rỗng ruột" khi đó là đối tượng danh mục
    // vừa được thêm đúng một trạng thái.
    [Fact]
    public void EntityMap_KeepsASingleStateOnTheSubmitPath()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            new EntityMapRow
            {
                Entity = "Khóa học",
                Included = true,
                States = new List<EntityLifecycleState> { new() { State = "Ngừng sử dụng", EntryCondition = "Admin ngừng khóa" } }
            }
        });

        Assert.Equal("Ngừng sử dụng", Assert.Single(Assert.Single(rows).States).State);
    }

    // Trần vẫn áp cho cả dòng tự thêm — trình duyệt chặn ngay tại nút bấm, đây là lưới thứ hai.
    [Fact]
    public void EntityMap_KeepsTheCapsForRowsTheUserAdded()
    {
        var rows = EntityMapBuilder.Sanitize(Enumerable.Range(1, EntityMapBuilder.MaxRows + 3)
            .Select(i => new EntityMapRow { Entity = $"Đối tượng {i}", Included = true, AddedByUser = true })
            .ToList());

        Assert.Equal(EntityMapBuilder.MaxRows, rows.Count);
    }
}
