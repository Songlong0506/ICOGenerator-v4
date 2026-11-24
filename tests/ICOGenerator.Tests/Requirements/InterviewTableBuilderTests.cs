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

    // Một "luồng" một bước là một câu mô tả, không phải luồng: nó không thành được chuyển trạng thái nào và cũng
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

    // Thứ tự bước là DỮ LIỆU ở bảng này (nó đi thẳng vào quy tắc chuyển trạng thái của spec), nên nút ↑ ↓ chỉ có nghĩa nếu
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

    [Fact]
    public void Serialize_GiuNguyenTiengVietVaDocLaiDuocCaBanCuEscape()
    {
        var rows = new List<FlowMapRow>
        {
            new()
            {
                Name = "Thu hồi tủ của nhân viên nghỉ việc",
                Kind = FlowKind.Happy,
                Role = "FCM",
                Steps =
                {
                    new FlowMapStep { Actor = "FCM", Action = "Đi thu hồi tủ", Included = true },
                    new FlowMapStep { Actor = "FCM", Action = "Xác nhận tủ sẵn sàng", Outcome = "Tủ chuyển sang Available", Included = true }
                }
            }
        };

        var json = FlowMapBuilder.Serialize(rows);

        Assert.Contains("\"Name\":\"Thu hồi tủ của nhân viên nghỉ việc\"", json);
        Assert.DoesNotContain("\\u", json);

        // Bản đã lưu trước thay đổi này (escape \uXXXX) phải đọc ra đúng cùng dữ liệu — không cần migration.
        var legacy = JsonSerializer.Serialize(rows);
        Assert.Contains("\\u", legacy);
        Assert.Equal(FlowMapBuilder.Parse(json)[0].Name, FlowMapBuilder.Parse(legacy)[0].Name);
        Assert.Equal(FlowMapBuilder.RenderConfirmedBlock(json), FlowMapBuilder.RenderConfirmedBlock(legacy));
    }
}
