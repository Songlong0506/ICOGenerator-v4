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

    [Fact]
    public void ScreenScope_LocksOnlyRowsThatCarryEvidence()
    {
        var rows = ScreenScopeMapBuilder.Build(new[]
        {
            new ScreenScopeRow { Screen = "Màn hình Training Plan", Locked = true, Evidence = "" }
        }, Scope);

        Assert.False(rows.Single(r => r.Screen == "Màn hình Training Plan").Locked);
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

    // PHÉP KIỂM MỐI NỐI: hai bảng đọc riêng đều "đạt", chỗ hỏng nằm ở chỗ nối. Một bước không màn hình nào
    // phụ trách nghĩa là hoặc người dùng không có chỗ nào để làm bước đó, hoặc bước đó không có thật.
    [Fact]
    public void ScreenScope_ReportsFlowStepsNoScreenCovers()
    {
        const string flowMap = """
            [{"name":"Đăng ký","kind":"luồng chính","steps":[
              {"action":"Gửi đơn đăng ký","included":true},
              {"action":"Duyệt đơn đăng ký","included":true}]}]
            """;
        var rows = new List<ScreenScopeRow>
        {
            new() { Screen = "Màn hình Training Plan", Included = true, FlowSteps = new List<string> { "Gửi đơn đăng ký" } }
        };

        var uncovered = ScreenScopeMapBuilder.UncoveredActions(rows, flowMap);

        Assert.Equal(new[] { "Duyệt đơn đăng ký" }, uncovered);
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
            new() { Screen = "A", Included = true, FlowSteps = new List<string> { "nhân viên gửi đơn đăng ký khóa học" } },
            new() { Screen = "B", Included = true, FlowSteps = new List<string> { "duyệt đơn" } }
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
            new() { Screen = "A", Included = true, FlowSteps = new List<string> { "Gửi đơn" } }
        };

        Assert.Empty(ScreenScopeMapBuilder.UncoveredActions(rows, flowMap));
    }

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

    // Ô "báo cho ai" để trống là một QUYẾT ĐỊNH (không gửi cho ai), không phải chỗ còn thiếu — và khối
    // ngữ cảnh phải nói ra điều đó, vì mặc định im lặng của các tầng sau là gửi cho tất cả.
    [Fact]
    public void EntityMap_ConfirmedBlockSpellsOutSilentTransitions()
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
                    new() { State = "Nháp", EntryCondition = "vừa tạo", Notify = "" },
                    new() { State = "Đã duyệt", EntryCondition = "quản lý duyệt", Notify = "người gửi" }
                }
            }
        });

        var block = EntityMapBuilder.RenderConfirmedBlock(System.Text.Json.JsonSerializer.Serialize(rows));

        Assert.Contains("không báo cho ai", block);
        Assert.Contains("báo cho người gửi", block);
    }
}
