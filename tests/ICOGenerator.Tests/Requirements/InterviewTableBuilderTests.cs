using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Ba builder của ba bảng chốt còn lại. Chúng là CHỐT CHẶN TẤT ĐỊNH cùng họ với PermissionMatrixBuilder và
// SourceColumnMapBuilder, nên test ở đây nhắm đúng các đường hỏng đã biết chứ không nhắm độ phủ:
//
//  • cờ tích/bỏ tích ở lượt BÀY BẢNG phải luôn là TÍCH SẴN bất kể model trả gì (structured output buộc
//    điền đủ trường, nên một model điền false cho có sẽ âm thầm bỏ tích sạch bảng);
//  • LUẬT BẰNG CHỨNG: không có trích dẫn thì không khóa được ô nào (bảng luồng và bảng màn hình không
//    nằm trong đó — cả hai đã bỏ hẳn cờ locked/evidence, xem docs/requirement-flow.md);
//  • bước/dòng NGƯỜI DÙNG BỎ vẫn có mặt trong tin nhắn gửi đi nhưng không lọt sang đường tiêu thụ nào;
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

    // Bước bị BỎ vẫn phải đi tới các đường tiêu thụ ở trạng thái đã loại — không lọt vào khối ngữ cảnh gắn
    // vào mọi lượt chat sau, cũng không lọt vào danh sách bước mà bảng màn hình phải phủ. Lọt một bước bịa
    // vào đó là BA hỏi lại đúng thứ người dùng vừa đóng, và spec chấm POC theo một bước không có thật.
    [Fact]
    public void FlowMap_DroppedStepsNeverReachTheConsumers()
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
        var json = JsonSerializer.Serialize(rows);

        Assert.DoesNotContain("Kế toán ghi sổ", FlowMapBuilder.RenderConfirmedBlock(json));
        Assert.DoesNotContain("Kế toán ghi sổ", FlowMapBuilder.IncludedActions(json));
        Assert.Contains("Gửi đơn", FlowMapBuilder.IncludedActions(json));
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

    // Người nhận thông báo thuộc hẳn bảng THÔNG BÁO, nên khối ngữ cảnh của bảng đối tượng không được nhắc
    // tới nó. Hai khối cùng nói về một quyết định là cách chắc chắn nhất để BA hỏi lại thứ người dùng vừa
    // chốt ở bảng kia.
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
                    new() { State = "Đã duyệt", EntryCondition = "quản lý duyệt" }
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

    // ==== HAI TRỤC CỦA MỘT THÔNG TIN: kiểu nhập ⇄ nguồn danh sách ====

    private static List<EntityMapRow> OneField(EntityFieldNote field, bool submit = true)
    {
        var rows = new[]
        {
            new EntityMapRow
            {
                Entity = "Bản mô tả công việc",
                Included = true,
                Fields = new List<EntityFieldNote> { field }
            }
        };
        return submit ? EntityMapBuilder.Sanitize(rows) : EntityMapBuilder.Build(rows);
    }

    // Dữ liệu của các dự án chốt bảng TRƯỚC khi hai trục tồn tại: JSON không có trường nào trong số đó, và
    // mặc định phải là ô gõ tay không ràng buộc. Bất kỳ mặc định nào khác là bịa ra một quyết định mà người
    // dùng chưa bao giờ được hỏi — "chọn 1" thì POC dựng dropdown rỗng, "bắt buộc" thì biểu mẫu không gửi
    // được.
    [Fact]
    public void EntityField_DefaultsToPlainTextForRowsSavedBeforeTheTwoAxesExisted()
    {
        var legacy = """
        [{"entity":"Bản mô tả công việc","included":true,"fields":[{"name":"Mã JD","meaning":"","used":true}]}]
        """;

        var field = EntityMapBuilder.Parse(legacy).Single().Fields.Single();
        Assert.Equal(EntityFieldInput.Text, field.Input);
        Assert.Equal(string.Empty, field.Source);
        Assert.False(field.Required);
        Assert.Empty(field.Options);
    }

    // Kiểu không phải ô CHỌN ⇒ cả ba ô của nhánh nguồn bị cắt. Giữ lại là để một quyết định chết nằm trong
    // DB rồi đi vào spec ở lượt sau, khi không còn ai nhớ nó thuộc về một kiểu đã bị đổi.
    [Fact]
    public void EntityField_ClearsTheListSourceWhenTheInputIsNotAChoice()
    {
        var field = OneField(new EntityFieldNote
        {
            Name = "Ghi chú",
            Used = true,
            Input = EntityFieldInput.Text,
            Source = EntityFieldSource.External,
            SourceSystem = "HR_Portal",
            Options = new List<string> { "A", "B" },
            Rule = "HcP-JD-XXX"
        }).Single().Fields.Single();

        Assert.Equal(string.Empty, field.Source);
        Assert.Equal(string.Empty, field.SourceSystem);
        Assert.Equal(string.Empty, field.Rule);
        Assert.Empty(field.Options);
    }

    // Ba nhánh nguồn loại trừ nhau: giữ ô của nhánh KHÔNG được chọn là bày ra hai câu trả lời cho cùng một
    // câu hỏi, và tầng sau không có cách nào biết cái nào là thật.
    [Fact]
    public void EntityField_KeepsOnlyTheCellOfTheChosenSourceBranch()
    {
        var field = OneField(new EntityFieldNote
        {
            Name = "OrgUnit",
            Used = true,
            Input = EntityFieldInput.ChoiceOne,
            Source = EntityFieldSource.App,
            SourceSystem = "HR_Portal",
            Options = new List<string> { "Nhà máy 1" }
        }).Single().Fields.Single();

        Assert.Equal(EntityFieldSource.App, field.Source);
        Assert.Equal(string.Empty, field.SourceSystem);
        Assert.Empty(field.Options);
    }

    // Nguồn lạ (model bịa ra một giá trị ngoài ba giá trị hợp lệ) rơi về CHƯA CHỐT, không rơi về một nguồn
    // nào đó cho có: "chưa chốt" thì BA hỏi nốt, còn đoán bừa thì không ai hỏi nữa.
    [Fact]
    public void EntityField_FallsBackToUnsetForAnUnknownSource()
    {
        var field = OneField(new EntityFieldNote
        {
            Name = "JobTitle",
            Used = true,
            Input = "single-select",
            Source = "lookup"
        }).Single().Fields.Single();

        Assert.Equal(EntityFieldInput.Text, field.Input);
        Assert.Equal(string.Empty, field.Source);
    }

    // "Bắt buộc nhập" một ô người dùng KHÔNG nhập là ô vô nghĩa — và nó sẽ thành một quy tắc validation chặn
    // đúng biểu mẫu mà nó đứng trong.
    [Fact]
    public void EntityField_DropsRequiredOnAnAutoGeneratedField()
    {
        var field = OneField(new EntityFieldNote
        {
            Name = "Mã JD",
            Used = true,
            Required = true,
            Input = EntityFieldInput.Auto,
            Rule = "HcP-JD-XXX"
        }).Single().Fields.Single();

        Assert.False(field.Required);
        Assert.Equal("HcP-JD-XXX", field.Rule);
    }

    // Danh sách nhập tại chỗ: bỏ rỗng, bỏ trùng (giữ chính tả người dùng gõ — chữ ấy lên thẳng dropdown của
    // POC), cắt theo trần.
    [Fact]
    public void EntityField_NormalizesTheInlineOptionList()
    {
        var field = OneField(new EntityFieldNote
        {
            Name = "JobFunction",
            Used = true,
            Input = EntityFieldInput.ChoiceMany,
            Source = EntityFieldSource.Inline,
            Options = new[] { "Option2", "  ", "option2", "Option5" }
                .Concat(Enumerable.Range(1, EntityMapBuilder.MaxOptionsPerField).Select(i => $"Thêm {i}"))
                .ToList()
        }).Single().Fields.Single();

        Assert.Equal(EntityMapBuilder.MaxOptionsPerField, field.Options.Count);
        Assert.Equal(new[] { "Option2", "Option5" }, field.Options.Take(2));
    }

    // Ô chọn chưa chốt được nguồn KHÔNG chặn nút gửi (khác bảng thông báo — ở đó dòng khóa không có checkbox
    // nên người dùng không có đường nào thoát ra ngoài việc điền). Đường thu hồi là một câu hỏi của BA, nên
    // cả hai bản kể phải NÓI RA nó — im lặng ở đây là để một dropdown không ai dựng được đi thẳng vào spec.
    [Fact]
    public void EntityMap_SpellsOutChoiceFieldsThatNeverSettledTheirSource()
    {
        var rows = OneField(new EntityFieldNote
        {
            Name = "JobTitle",
            Used = true,
            Input = EntityFieldInput.ChoiceMany
        });

        var block = EntityMapBuilder.RenderConfirmedBlock(JsonSerializer.Serialize(rows));
        Assert.Contains("chưa rõ danh sách lấy ở đâu: JobTitle", block);
        Assert.Contains("hỏi nốt", block);
        Assert.Contains("chưa rõ danh sách lấy ở đâu: JobTitle", EntityMapBuilder.RenderUserMessage(rows));
    }

    // "Lấy từ hệ thống khác" mà không nói hệ thống nào là một tích hợp không hành động được: POC không biết
    // lấy dữ liệu mẫu ở đâu và spec ghi một nguồn không có thật.
    [Fact]
    public void EntityMap_SpellsOutAnExternalSourceWithNoSystemNamed()
    {
        var rows = OneField(new EntityFieldNote
        {
            Name = "JobTitle",
            Used = true,
            Input = EntityFieldInput.ChoiceMany,
            Source = EntityFieldSource.External
        });

        Assert.Contains("chưa rõ lấy từ hệ thống nào: JobTitle",
            EntityMapBuilder.RenderConfirmedBlock(JsonSerializer.Serialize(rows)));
    }

    // Các ràng buộc phải đi được tới đầu đọc: khối ngữ cảnh là thứ prompt sinh AI Design Spec đọc để dựng
    // "## 8. Data Model Summary", nên một ô chọn kể lại thành ô gõ tay là mất trọn phần người dùng vừa chốt.
    [Fact]
    public void EntityMap_CarriesTheConstraintsIntoTheConfirmedBlock()
    {
        var rows = OneField(new EntityFieldNote
        {
            Name = "JobFunction",
            Used = true,
            Required = true,
            Input = EntityFieldInput.ChoiceMany,
            Source = EntityFieldSource.Inline,
            Options = new List<string> { "Option2", "Option5" }
        });

        var block = EntityMapBuilder.RenderConfirmedBlock(JsonSerializer.Serialize(rows));
        Assert.Contains("bắt buộc", block);
        Assert.Contains("chọn nhiều giá trị", block);
        Assert.Contains("danh sách cố định: Option2, Option5", block);
    }

    // Ô gõ tay không bắt buộc là ca THƯỜNG GẶP NHẤT: dán một chú thích vào mọi dòng của một bảng mười hai
    // dòng là làm loãng đúng những dòng CÓ ràng buộc thật.
    [Fact]
    public void EntityMap_SaysNothingExtraAboutAPlainTextField()
    {
        var rows = OneField(new EntityFieldNote { Name = "Ghi chú", Used = true, Meaning = "mô tả thêm" });

        Assert.Contains("Ghi chú (mô tả thêm)", EntityMapBuilder.RenderConfirmedBlock(JsonSerializer.Serialize(rows)));
        Assert.DoesNotContain("[", EntityMapBuilder.RenderConfirmedBlock(JsonSerializer.Serialize(rows)));
    }

    // ==== MÀN HÌNH DANH MỤC mà bảng này đẻ ra ====

    // "Ứng dụng tự quản lý" nghĩa là ứng dụng phải có một màn hình CRUD cho danh mục đó, và chính hàm này
    // là lý do bảng đối tượng đứng TRƯỚC bảng màn hình trong thứ tự phụ thuộc — không gieo ra thì màn hình
    // ấy không có dòng nào trong bảng phân quyền và không có mục nào ở "## 6. Screens To Generate".
    [Fact]
    public void EntityMap_TurnsAppManagedListsIntoScreens()
    {
        var screens = EntityMapBuilder.ManagedListScreens(new[]
        {
            new EntityMapRow
            {
                Entity = "Bản mô tả công việc",
                Included = true,
                Fields = new List<EntityFieldNote>
                {
                    new() { Name = "OrgUnit", Used = true, Input = EntityFieldInput.ChoiceOne, Source = EntityFieldSource.App },
                    new() { Name = "JobTitle", Used = true, Input = EntityFieldInput.ChoiceMany, Source = EntityFieldSource.External, SourceSystem = "HR_Portal" },
                    new() { Name = "PC Level", Used = true }
                }
            }
        });

        Assert.Equal(new[] { "OrgUnit Catalog" }, screens);
    }

    // Cùng một danh mục dùng ở nhiều đối tượng vẫn chỉ là MỘT màn hình — gieo hai lần là bắt người dùng rà
    // hai dòng cho cùng một thứ. Dòng bị bỏ tích thì không gieo gì: họ vừa loại nó.
    [Fact]
    public void EntityMap_DeduplicatesManagedListsAndSkipsDroppedRows()
    {
        var appList = () => new EntityFieldNote
        {
            Name = "OrgUnit",
            Used = true,
            Input = EntityFieldInput.ChoiceOne,
            Source = EntityFieldSource.App
        };

        var screens = EntityMapBuilder.ManagedListScreens(new[]
        {
            new EntityMapRow { Entity = "Bản mô tả công việc", Included = true, Fields = new List<EntityFieldNote> { appList() } },
            new EntityMapRow { Entity = "Nhân viên", Included = true, Fields = new List<EntityFieldNote> { appList() } },
            new EntityMapRow
            {
                Entity = "Hồ sơ cũ",
                Included = false,
                Fields = new List<EntityFieldNote>
                {
                    new() { Name = "Phân loại", Used = true, Input = EntityFieldInput.ChoiceOne, Source = EntityFieldSource.App }
                }
            }
        });

        Assert.Equal(new[] { "OrgUnit Catalog" }, screens);
    }

    // Người dùng chỉ tích một ô nhỏ trong bảng này, nên bản kể phải NÓI RA rằng họ vừa đặt hàng thêm màn
    // hình — bản kể ấy là thứ mọi tầng chắt lọc phía sau đọc, không phải cột DB.
    [Fact]
    public void EntityMap_UserMessageNamesTheManagedLists()
    {
        var rows = OneField(new EntityFieldNote
        {
            Name = "OrgUnit",
            Used = true,
            Input = EntityFieldInput.ChoiceOne,
            Source = EntityFieldSource.App
        });

        Assert.Contains("OrgUnit Catalog", EntityMapBuilder.RenderUserMessage(rows));
    }

    // ==== QUAN HỆ CHA-CON: một "thông tin" thật ra là NHIỀU DÒNG ====

    private static EntityMapRow Parent(string name = "Bản mô tả công việc") => new()
    {
        Entity = name,
        Included = true,
        Fields = new List<EntityFieldNote> { new() { Name = "Mã JD", Used = true } }
    };

    private static EntityMapRow Child(string name, string parent, int? min = null, int? max = null) => new()
    {
        Entity = name,
        Included = true,
        ParentEntity = parent,
        MinRows = min,
        MaxRows = max,
        Fields = new List<EntityFieldNote>
        {
            new() { Name = "Nội dung", Used = true },
            new() { Name = "Tỷ trọng %", Used = true, Input = EntityFieldInput.Number }
        }
    };

    // Ca gốc: Trách nhiệm là các dòng của JD, mỗi JD đúng 5 dòng. Cả hai bản kể phải nói ra quan hệ đó —
    // thiếu nó thì spec dựng Trách nhiệm thành một hồ sơ độc lập có màn hình CRUD riêng.
    [Fact]
    public void EntityMap_KeepsAChildRowsRelation()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            Parent(),
            Child("Trách nhiệm", "Bản mô tả công việc", min: 5, max: 5)
        });

        var child = rows.Single(r => r.Entity == "Trách nhiệm");
        Assert.Equal("Bản mô tả công việc", child.ParentEntity);
        Assert.Equal(5, child.MinRows);
        Assert.Equal(5, child.MaxRows);

        var block = EntityMapBuilder.RenderConfirmedBlock(JsonSerializer.Serialize(rows));
        Assert.Contains("là các DÒNG của \"Bản mô tả công việc\"", block);
        Assert.Contains("có 5 dòng", block);
        Assert.Contains("là các DÒNG của \"Bản mô tả công việc\"", EntityMapBuilder.RenderUserMessage(rows));
    }

    // Cha bịa (hoặc gõ sai tên) ⇒ HẠ về hồ sơ độc lập, KHÔNG loại dòng: một quan hệ sai là một ô điền sai,
    // còn nuốt cả dòng là làm biến mất một đối tượng người dùng đã tích.
    [Fact]
    public void EntityMap_DropsARelationPointingAtNothing()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            Parent(),
            Child("Trách nhiệm", "Đối tượng không có thật", min: 5)
        });

        var child = rows.Single(r => r.Entity == "Trách nhiệm");
        Assert.Equal(string.Empty, child.ParentEntity);
        Assert.Null(child.MinRows);
    }

    // Chính tả lấy của BẢNG, không của model — cùng luật với ScreenScopeMapBuilder.MatchScreen. Hai cách
    // viết cho cùng một đối tượng thì mọi tầng sau tưởng là hai đối tượng.
    [Fact]
    public void EntityMap_TakesTheParentSpellingFromTheTable()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            Parent(),
            Child("Trách nhiệm", "  bản mô tả CÔNG VIỆC  ")
        });

        Assert.Equal("Bản mô tả công việc", rows.Single(r => r.Entity == "Trách nhiệm").ParentEntity);
    }

    [Fact]
    public void EntityMap_RefusesAnEntityThatIsItsOwnParent()
    {
        var rows = EntityMapBuilder.Sanitize(new[] { Child("Trách nhiệm", "Trách nhiệm") });

        Assert.Equal(string.Empty, rows.Single().ParentEntity);
    }

    // TỐI ĐA MỘT CẤP. A→B→C: giữ B→C (cấp gần gốc nhất) và cắt A. POC dựng grid lồng grid là thứ không ai
    // duyệt nổi, và người dùng nghiệp vụ không mô hình hoá ba tầng.
    [Fact]
    public void EntityMap_AllowsOnlyOneLevelOfNesting()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            Parent("Đơn hàng"),
            Child("Dòng hàng", "Đơn hàng"),
            Child("Chi tiết dòng hàng", "Dòng hàng")
        });

        Assert.Equal("Đơn hàng", rows.Single(r => r.Entity == "Dòng hàng").ParentEntity);
        Assert.Equal(string.Empty, rows.Single(r => r.Entity == "Chi tiết dòng hàng").ParentEntity);
    }

    // Chu trình tự vỡ nhờ chính luật một cấp: cả hai dòng đều có cha-có-cha nên cả hai về độc lập. Không
    // vòng lặp nào, không thứ tự duyệt nào quyết định kết quả.
    [Fact]
    public void EntityMap_BreaksACycleInsteadOfLoopingForever()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            Child("A", "B"),
            Child("B", "A")
        });

        Assert.All(rows, r => Assert.Equal(string.Empty, r.ParentEntity));
    }

    // Cùng một bảng gửi hai lần phải ra cùng một kết quả: luật một cấp đọc ảnh chụp TRƯỚC khi sửa, nên thứ
    // tự các dòng trong payload không đổi được ai giữ quan hệ.
    [Fact]
    public void EntityMap_ResolvesRelationsIndependentlyOfRowOrder()
    {
        var forward = EntityMapBuilder.Sanitize(new[]
        {
            Parent("Đơn hàng"), Child("Dòng hàng", "Đơn hàng"), Child("Chi tiết", "Dòng hàng")
        });
        var reversed = EntityMapBuilder.Sanitize(new[]
        {
            Child("Chi tiết", "Dòng hàng"), Child("Dòng hàng", "Đơn hàng"), Parent("Đơn hàng")
        });

        Assert.Equal("Đơn hàng", forward.Single(r => r.Entity == "Dòng hàng").ParentEntity);
        Assert.Equal("Đơn hàng", reversed.Single(r => r.Entity == "Dòng hàng").ParentEntity);
        Assert.Equal(string.Empty, forward.Single(r => r.Entity == "Chi tiết").ParentEntity);
        Assert.Equal(string.Empty, reversed.Single(r => r.Entity == "Chi tiết").ParentEntity);
    }

    // Cha bị người dùng BỎ TÍCH thì quan hệ không còn chỗ đứng — nó sẽ trỏ vào một đối tượng không đi vào
    // ứng dụng. Hạ về độc lập, vẫn không loại dòng.
    [Fact]
    public void EntityMap_DropsARelationWhoseParentWasUnticked()
    {
        var parent = Parent();
        parent.Included = false;

        var rows = EntityMapBuilder.Sanitize(new[] { parent, Child("Trách nhiệm", "Bản mô tả công việc") });

        Assert.Equal(string.Empty, rows.Single(r => r.Entity == "Trách nhiệm").ParentEntity);
    }

    // Số dòng chỉ có nghĩa dưới một quan hệ: rơi lại trên một hồ sơ độc lập, nó đọc như một ràng buộc về số
    // bản ghi của cả ứng dụng — thứ chưa ai từng hỏi.
    [Fact]
    public void EntityMap_ClearsTheRowCountOnAStandaloneEntity()
    {
        var row = Parent();
        row.MinRows = 3;
        row.MaxRows = 7;

        var saved = EntityMapBuilder.Sanitize(new[] { row }).Single();
        Assert.Null(saved.MinRows);
        Assert.Null(saved.MaxRows);
    }

    // Gõ nhầm thứ tự hai ô là ca thường gặp nhất — đổi chỗ giữ được cả hai con số họ đã gõ, bỏ một trong
    // hai thì mất một nửa thông tin mà không lời nào nói.
    [Fact]
    public void EntityMap_SwapsAnInvertedRowCountRange()
    {
        var child = EntityMapBuilder.Sanitize(new[]
        {
            Parent(), Child("Trách nhiệm", "Bản mô tả công việc", min: 9, max: 2)
        }).Single(r => r.Entity == "Trách nhiệm");

        Assert.Equal(2, child.MinRows);
        Assert.Equal(9, child.MaxRows);
    }

    [Fact]
    public void EntityMap_DropsARowCountOutsideTheSaneRange()
    {
        var child = EntityMapBuilder.Sanitize(new[]
        {
            Parent(), Child("Trách nhiệm", "Bản mô tả công việc", min: -1, max: EntityMapBuilder.MaxChildRowCount + 1)
        }).Single(r => r.Entity == "Trách nhiệm");

        Assert.Null(child.MinRows);
        Assert.Null(child.MaxRows);
    }

    // Model BỎ TRỐNG cả ba trường quan hệ (ca thường gặp nhất — phần lớn đối tượng là hồ sơ độc lập) ⇒ dòng
    // ra ở đúng trạng thái độc lập, không phải một quan hệ treo lơ lửng. Cùng luật với giá trị khởi tạo
    // `Input = text`: mặc định phải là thứ an toàn, vì đây là thứ model trả về khi nó không có gì để nói.
    [Fact]
    public void EntityMap_ReadsRowsWithNoRelationFieldsAsStandalone()
    {
        var noRelation = """
        [{"entity":"Bản mô tả công việc","included":true,"fields":[{"name":"Mã JD","used":true}]}]
        """;

        var row = EntityMapBuilder.Parse(noRelation).Single();
        Assert.Equal(string.Empty, row.ParentEntity);
        Assert.Null(row.MinRows);
        Assert.Null(row.MaxRows);
    }

    // ==== BẢN KỂ CỦA BẢNG KHÔNG ĐƯỢC CHỞ VĂN XUÔI CỦA BA ====
    // Ca thật (JD Library 1). BA điền ô mô tả của đối tượng JD là "Mô tả công việc được Manager tạo, kiểm
    // tra, verify và approve trước khi dùng để gán cho nhân viên", trong khi chính người dùng đã kể ở khung
    // chat và đã TỰ TAY rà ở bảng luồng rằng HRBP verify rồi HoD của Manager approve. Ô mô tả nằm cạnh tên
    // đối tượng như một cái nhãn xám nên người dùng đọc lướt rồi bấm gửi — và bản kể do RenderUserMessage
    // soạn được lưu dưới VAI CỦA HỌ. Từ đó câu của BA là "lời người dùng" với mọi tầng phía sau:
    //
    //  1. Bộ chắt "điểm cần làm rõ" đẻ ra mục "Chưa rõ ai thực hiện verify và approve JD", CoveragePendingGuard
    //     hạ ba dòng bản đồ («Đối tượng người dùng & vai trò», «Chức năng & luồng nghiệp vụ chính», «Vòng đời
    //     & trạng thái») xuống [MỘT PHẦN], và RequirementReadinessGate KHÓA nút "Write Requirement" — ở đúng
    //     lượt mà BA vừa nói "các nhóm thông tin chính đã đủ".
    //  2. Bộ chắt bản đồ bao phủ trích thẳng câu đó làm {nguồn: …}, tức ký tên người dùng vào câu của BA.
    //  3. BA đem nó ra chất vấn: "trong bảng luồng anh/chị đã chốt HRBP verify và HoD approve, nhưng phần mô
    //     tả JD lại ghi Manager thực hiện verify và approve; luồng nào đúng ạ?" — một mâu thuẫn không có thật
    //     (hai vế không cùng nguồn), đốt trọn một lượt vốn phải đứng MỘT MÌNH, và mở đường cho người dùng lật
    //     chính luồng họ đã duyệt.
    //
    // Mô tả vẫn được LƯU (nó là văn xuôi cho ## 8. Data Model Summary), chỉ không được đóng dấu thành lời
    // người dùng ở bất kỳ đường nào.

    [Fact]
    public void EntityMap_UserMessageDoesNotEchoTheDescriptionBaWrote()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            new EntityMapRow
            {
                Entity = "JD",
                Description = "Mô tả công việc được Manager tạo, kiểm tra, verify và approve",
                Included = true,
                Fields = new List<EntityFieldNote> { new() { Name = "Job Title", Used = true } }
            }
        });

        var message = EntityMapBuilder.RenderUserMessage(rows);

        Assert.Contains("JD:", message);
        Assert.Contains("Job Title", message);
        Assert.DoesNotContain("verify và approve", message);
    }

    // Khối ngữ cảnh thì ngược lại: mô tả vẫn phải có mặt (bước sinh spec đọc nó), nhưng đứng ở DÒNG RIÊNG có
    // gắn xuất xứ. Nối vào dòng tên đối tượng dưới tiêu đề "đã được NGƯỜI DÙNG CHỐT" là đúng cách một câu văn
    // xuôi của BA trở thành bằng chứng.
    [Fact]
    public void EntityMap_ConfirmedBlockMarksTheDescriptionAsBaWritten()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            new EntityMapRow
            {
                Entity = "JD",
                Description = "Mô tả công việc được Manager tạo, kiểm tra, verify và approve",
                Included = true,
                Fields = new List<EntityFieldNote> { new() { Name = "Job Title", Used = true } }
            }
        });

        var block = EntityMapBuilder.RenderConfirmedBlock(JsonSerializer.Serialize(rows));

        Assert.Contains("* JD\n", block!.Replace("\r\n", "\n"));
        Assert.Contains("mô tả (BA tự đặt, chưa ai rà): Mô tả công việc được Manager", block);
        Assert.Contains("KHÔNG phải lời người dùng", block);
        Assert.Contains("đừng lấy nó làm một vế mâu thuẫn", block);
    }

    // Câu dặn về ô mô tả chỉ có nghĩa khi khối có ít nhất một dòng mô tả: khối này đi kèm MỌI lượt chat sau,
    // nên một dòng lệnh nói về thứ không tồn tại trong khối là token thừa ở mọi lượt.
    [Fact]
    public void EntityMap_ConfirmedBlockOmitsTheCaptionWarningWhenNoRowHasOne()
    {
        var rows = EntityMapBuilder.Sanitize(new[]
        {
            new EntityMapRow
            {
                Entity = "JD",
                Included = true,
                Fields = new List<EntityFieldNote> { new() { Name = "Job Title", Used = true } }
            }
        });

        var block = EntityMapBuilder.RenderConfirmedBlock(JsonSerializer.Serialize(rows));

        Assert.DoesNotContain("KHÔNG phải lời người dùng", block);
    }

    // Ô "việc của màn" là cùng một hình dạng lỗi: BA điền sẵn, người dùng đọc như nhãn, bản kể lưu dưới vai
    // của họ. Chặn ở một bảng mà bỏ bảng kia là để nguyên đúng đường cũ, chỉ đổi tên ô.
    [Fact]
    public void ScreenScope_UserMessageDoesNotEchoThePurposeBaWrote()
    {
        var rows = ScreenScopeMapBuilder.Sanitize(new[]
        {
            new ScreenScopeRow
            {
                Screen = "Màn hình Training Plan",
                Purpose = "Nơi HR duyệt và chốt kế hoạch năm",
                Included = true
            }
        }, Scope);

        var message = ScreenScopeMapBuilder.RenderUserMessage(rows);

        Assert.Contains("Màn hình Training Plan", message);
        Assert.DoesNotContain("HR duyệt và chốt", message);
    }

    [Fact]
    public void ScreenScope_ConfirmedBlockMarksThePurposeAsBaWritten()
    {
        var rows = ScreenScopeMapBuilder.Sanitize(new[]
        {
            new ScreenScopeRow
            {
                Screen = "Màn hình Training Plan",
                Purpose = "Nơi HR duyệt và chốt kế hoạch năm",
                Included = true
            }
        }, Scope);

        var block = ScreenScopeMapBuilder.RenderConfirmedBlock(JsonSerializer.Serialize(rows));

        Assert.Contains("việc của màn (BA tự đặt, chưa ai rà): Nơi HR duyệt", block);
        Assert.Contains("KHÔNG phải lời người dùng", block);
    }
}
