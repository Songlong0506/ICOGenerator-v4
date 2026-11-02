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

    // ==== BẢNG LUỒNG: phần NGƯỜI DÙNG TỰ THÊM và THỨ TỰ BƯỚC ====

    // Thứ tự bước là DỮ LIỆU ở bảng này (nó đi thẳng vào oracle chấm POC), nên nút ↑ ↓ chỉ có nghĩa nếu
    // server giữ nguyên thứ tự client gửi lên. Một builder "chuẩn hoá" mà xếp lại bước là làm cả thao tác
    // đổi chỗ trên bảng thành vô hình.
    [Fact]
    public void FlowMap_KeepsTheStepOrderTheBrowserSentUp()
    {
        var rows = FlowMapBuilder.Sanitize(new[]
        {
            new FlowMapRow
            {
                Name = "Tạo và phê duyệt JD",
                Steps = new List<FlowMapStep>
                {
                    new() { Action = "Submit JD" },
                    new() { Action = "Tạo JD" },
                    new() { Action = "Verify JD" }
                }
            }
        });

        Assert.Equal(new[] { "Submit JD", "Tạo JD", "Verify JD" }, rows.Single().Steps.Select(s => s.Action));
    }

    // Bước người dùng tự gõ không đi qua danh sách cho phép nào (khác bảng màn hình) — nhưng nó phải giữ
    // được cờ nguồn gốc tới tận tin nhắn gửi đi. Một bước chưa từng có trong đề xuất mà lặng lẽ đi vào
    // phạm vi là thứ phải nói ra, và ở bảng này còn hơn thế: mỗi bước được giữ là một mục IncludedActions
    // mà bảng màn hình sau đó buộc phải có chức năng phụ trách.
    [Fact]
    public void FlowMap_UserMessageNamesTheStepsTheUserAdded()
    {
        var rows = FlowMapBuilder.Sanitize(new[]
        {
            new FlowMapRow
            {
                Name = "Tạo và phê duyệt JD",
                Steps = new List<FlowMapStep>
                {
                    new() { Action = "Tạo JD" },
                    new() { Action = "Gửi mail cho HRBP", AddedByUser = true }
                }
            }
        });

        var message = FlowMapBuilder.RenderUserMessage(rows);
        Assert.True(rows.Single().Steps[1].AddedByUser);
        Assert.Contains("Các bước mình tự bổ sung", message);
        Assert.Contains("Gửi mail cho HRBP", message);
    }

    // Luồng tự thêm chỉ kể TÊN LUỒNG: mọi bước trong đó cũng là bước người dùng tự gõ, liệt kê ra là in
    // lại nguyên danh sách vừa ở ngay bên trên.
    [Fact]
    public void FlowMap_UserMessageNamesTheFlowTheUserAddedWithoutRelistingItsSteps()
    {
        var rows = FlowMapBuilder.Sanitize(new[]
        {
            new FlowMapRow
            {
                Name = "Thu hồi JD đã duyệt",
                AddedByUser = true,
                Steps = new List<FlowMapStep>
                {
                    new() { Action = "Chọn JD cần thu hồi", AddedByUser = true },
                    new() { Action = "Xác nhận thu hồi", AddedByUser = true }
                }
            }
        });

        var message = FlowMapBuilder.RenderUserMessage(rows);
        Assert.Contains("Các luồng mình tự bổ sung vào bảng: Thu hồi JD đã duyệt", message);
        Assert.DoesNotContain("Các bước mình tự bổ sung", message);
    }

    // Bỏ hết bước của một luồng tự thêm ⇒ không có gì đi vào phạm vi, nên cũng không có gì để kể. Kể tên
    // nó ở đây là báo với BA rằng người dùng vừa bổ sung một luồng mà họ vừa tự đóng lại.
    [Fact]
    public void FlowMap_UserMessageSkipsAnAddedFlowWithEveryStepDropped()
    {
        var rows = FlowMapBuilder.Sanitize(new[]
        {
            new FlowMapRow
            {
                Name = "Thu hồi JD đã duyệt",
                AddedByUser = true,
                Steps = new List<FlowMapStep>
                {
                    new() { Action = "Chọn JD cần thu hồi", AddedByUser = true, Included = false },
                    new() { Action = "Xác nhận thu hồi", AddedByUser = true, Included = false }
                }
            }
        });

        Assert.DoesNotContain("Các luồng mình tự bổ sung", FlowMapBuilder.RenderUserMessage(rows));
    }

    // Cờ "người dùng tự thêm" ở lượt BÀY BẢNG là cờ của MODEL — một chỗ để nó gán chữ ký của người dùng
    // lên luồng chính nó vừa bịa, rồi tin nhắn gửi đi kể lại y như thế. Đường bày bảng ép cờ về false.
    [Fact]
    public void FlowMap_ClearsTheUserAddedFlagOnTheProposalPath()
    {
        var rows = FlowMapBuilder.Build(new[]
        {
            new FlowMapRow
            {
                Name = "Tạo và phê duyệt JD",
                AddedByUser = true,
                Steps = new List<FlowMapStep>
                {
                    new() { Action = "Tạo JD", AddedByUser = true },
                    new() { Action = "Submit JD", AddedByUser = true }
                }
            }
        });

        var row = rows.Single();
        Assert.False(row.AddedByUser);
        Assert.All(row.Steps, s => Assert.False(s.AddedByUser));
    }

    // ==== BẢNG MÀN HÌNH ====

    private static readonly List<string> Scope = new() { "Màn hình Training Plan", "Trang duyệt của HOD" };

    // Màn hình model bỏ quên vẫn phải có mặt, và phải TÍCH SẴN: đưa vào ở trạng thái bỏ tích là ra quyết
    // định loại thay người dùng ở đúng chỗ họ không nhìn thấy để phản đối.
    [Fact]
    public void ScreenScope_AddsForgottenScreensTicked()
    {
        var rows = ScreenScopeMapBuilder.Build(null, 
            new[] { new ScreenScopeRow { Screen = "Màn hình Training Plan", Purpose = "Lập kế hoạch" } }, Scope);

        Assert.Equal(2, rows.Count);
        var forgotten = rows.Single(r => r.Screen == "Trang duyệt của HOD");
        Assert.True(forgotten.Included);
    }

    // Một dòng bịa lọt qua là một tính năng ngoài phạm vi đi vào tài liệu mang chữ ký người dùng.
    [Fact]
    public void ScreenScope_DropsScreensOutsideThePlannedScope()
    {
        var rows = ScreenScopeMapBuilder.Build(null, 
            new[] { new ScreenScopeRow { Screen = "Màn hình quản trị hệ thống" } }, Scope);

        Assert.DoesNotContain(rows, r => r.Screen == "Màn hình quản trị hệ thống");
    }

    // Cờ "người dùng tự thêm" chỉ có nghĩa ở đường GỬI. Ở lượt BÀY BẢNG mọi dòng đều do model soạn, nên đọc
    // cờ ở đây là dựng cho model một cửa sau đi vòng chốt chặn "màn hình bịa" bằng cách tự khai là người
    // dùng — mà đó đúng là thứ chốt chặn này sinh ra để chặn.
    [Fact]
    public void ScreenScope_IgnoresTheUserAddedFlagOnTheProposalPath()
    {
        var rows = ScreenScopeMapBuilder.Build(null, 
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
        var rows = ScreenScopeMapBuilder.Build(null, new[]
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

        var rows = ScreenScopeMapBuilder.Build(null, new[]
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
        var rows = ScreenScopeMapBuilder.Build(null, new[]
        {
            new ScreenScopeRow { Screen = "Màn hình Training Plan", Covers = new List<string> { "Trang duyệt của HOD" } },
            new ScreenScopeRow { Screen = "Trang duyệt của HOD" }
        }, Scope);

        Assert.Equal(Scope, rows.Select(r => r.Screen));
    }

    // Mục đã gộp không được QUAY LẠI ở lượt sau. Lượt chắt lọc là một lời gọi LLM chạy độc lập với bảng
    // nên nó vẫn nhả ra mục cũ; chốt chặn này là thứ bảo đảm mục ấy không mọc lại thành một dòng của bảng
    // phân quyền và một màn hình của bản demo.
    [Fact]
    public void ScreenScope_MergeDoesNotResurrectCoveredItems()
    {
        const string table = """
            [{"screen":"Trang Training Plan Detail","included":true,"confirmedByUser":true,
              "covers":["Tính năng Generate Training Implement từ Training Plan Detail"]}]
            """;

        var merged = ScreenScopeMapBuilder.Merge(table, new[]
        {
            new ScopeAddition { Screen = "Tính năng Generate Training Implement từ Training Plan Detail" }
        });

        Assert.Null(merged);
    }

    // Dòng người dùng đã BỎ TÍCH là một tấm BIA: lượt chắt lọc gặp lại đúng cái tên ấy cũng không dựng lại
    // được. Không có luật này thì mỗi màn hình bị loại quay lại làm một mục "mới" ở lượt kế, và cổng mở lại
    // vĩnh viễn.
    [Fact]
    public void ScreenScope_MergeNeverRebuildsAScreenTheUserRemoved()
    {
        const string table = """
            [{"screen":"Trang duyệt của HOD","included":false,"confirmedByUser":true}]
            """;

        var merged = ScreenScopeMapBuilder.Merge(table, new[]
        {
            new ScopeAddition { Screen = "Trang duyệt của HOD", Functions = new List<string> { "Duyệt kế hoạch" } }
        });

        Assert.Null(merged);
    }

    // CHỨC NĂNG mới trên một màn hình ĐÃ CHỐT: ca mà bản cũ không biểu diễn nổi (phép so chỉ nhìn tên màn
    // hình, nên cả màn hình vẫn "đã biết" và cổng không mở lại). Nay nó là một mục chờ duyệt như mọi mục
    // khác — và phần người dùng đã rà thì không bị đụng tới.
    [Fact]
    public void ScreenScope_MergeAddsANewFunctionToAConfirmedScreen()
    {
        const string table = """
            [{"screen":"Màn hình Training Plan","purpose":"Lập kế hoạch","included":true,"confirmedByUser":true,
              "functions":[{"name":"Tạo version plan","included":true,"confirmedByUser":true}]}]
            """;

        var merged = ScreenScopeMapBuilder.Merge(table, new[]
        {
            new ScopeAddition
            {
                Screen = "Màn hình Training Plan",
                Purpose = "Model đoán lại việc của màn",
                Functions = new List<string> { "Tạo version plan", "Xuất kế hoạch ra Excel" }
            }
        });

        var row = Assert.Single(merged!);
        Assert.Equal("Lập kế hoạch", row.Purpose);            // chỉ THÊM, không ghi đè
        Assert.True(row.ConfirmedByUser);
        Assert.Equal(new[] { "Tạo version plan", "Xuất kế hoạch ra Excel" }, row.Functions.Select(f => f.Name));
        Assert.True(row.Functions[0].ConfirmedByUser);        // chức năng cũ giữ nguyên dấu
        Assert.False(row.Functions[1].ConfirmedByUser);       // chức năng mới còn chờ duyệt
        Assert.True(ScreenScopeMapBuilder.HasPending(JsonSerializer.Serialize(merged)));
    }

    // Phạm vi HIỆU LỰC là mọi dòng CÒN TÍCH — chốt rồi hay còn chờ duyệt. Mục chưa chốt phải có mặt (một
    // màn hình lộ ra sau lượt chốt mà không vào được bảng phân quyền thì mặc nhiên "không ai được xem"),
    // mục đã bỏ tích thì không bao giờ.
    [Fact]
    public void ScreenScope_EffectiveScreensKeepsPendingRowsAndDropsUntickedOnes()
    {
        const string table = """
            [{"screen":"Màn hình Training Plan","included":true,"confirmedByUser":true},
             {"screen":"Trang duyệt của HOD","included":false,"confirmedByUser":true},
             {"screen":"Báo cáo tổng hợp","included":true,"confirmedByUser":false}]
            """;

        Assert.Equal(new[] { "Màn hình Training Plan", "Báo cáo tổng hợp" },
            ScreenScopeMapBuilder.EffectiveScreens(table));
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

    // ==== BẢN KỂ CỦA BẢNG KHÔNG ĐƯỢC CHỞ VĂN XUÔI CỦA BA ====
    // Ca thật (JD Library 1). BA điền sẵn một câu mô tả nói Manager tạo, kiểm tra, verify và approve JD, trong
    // khi chính người dùng đã kể ở khung chat và đã TỰ TAY rà ở bảng luồng rằng HRBP verify rồi HoD của
    // Manager approve. Ô văn xuôi BA điền nằm như một cái nhãn xám nên người dùng đọc lướt rồi bấm gửi — và
    // bản kể do RenderUserMessage soạn được lưu dưới VAI CỦA HỌ. Từ đó câu của BA là "lời người dùng" với
    // mọi tầng phía sau: bộ chắt điểm cần làm rõ hạ bản đồ xuống [MỘT PHẦN] và khóa nút "Write Requirement",
    // rồi BA đem nó ra chất vấn một mâu thuẫn không có thật (hai vế không cùng nguồn).

    // Ô "việc của màn" là đúng hình dạng lỗi đó: BA điền sẵn, người dùng đọc như nhãn, bản kể lưu dưới vai
    // của họ.
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
