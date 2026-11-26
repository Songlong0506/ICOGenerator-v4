using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Workflows;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// BẤT BIẾN của cổng tạo tài liệu: cổng mở ⇒ BA không còn câu hỏi nào đang chờ trả lời. Nó đúng vì lời mời
// (cột ReadinessVerified / cờ InvitesWriteRequirement) là đường mở DUY NHẤT, và lời mời đã qua chốt chặn
// "lượt đang hỏi thì hạ cờ" của ApplyReadinessGate.
//
// Ca thật đã phá bất biến này (dự án JD Library, 2026-11-25): Brief V1 đã duyệt, POC đã dựng, người dùng
// xin thêm "trang riêng để nhân viên xem JD được gán", BA hỏi lại "chỉ JD đang active hay cả JD đã gán trước
// đây?" — và ngay dưới thẻ hỏi là nút "Write Requirement" kèm câu "mình đã thu thập đủ thông tin". Thủ phạm là
// đường mở thứ hai "draft đã có + bản đồ bao phủ đủ", đường này không đọc lượt cuối. Nó sống ở HAI chỗ vẽ ra
// cổng (Index.cshtml lúc tải trang, requirements.js ở frame done), nên test giữ cả hai.
public class WriteRequirementGateInviteOnlyTests
{
    // Luật của bản server render sống ở WriteRequirementGate.Resolve (hàm thuần); view chỉ gọi nó.
    [Fact]
    public void ServerRender_OpensTheGateOnlyOnTheInvite()
    {
        var view = File.ReadAllText(FindRepoFile(Path.Combine("Views", "Requirements", "Index.cshtml")));

        Assert.Contains("WriteRequirementGate.Resolve(", view);
        Assert.DoesNotContain("coverageReady", view);
        Assert.DoesNotContain("data-draft-exists", view);
    }

    // Đúng ca thật ở đầu file: Brief đã có (bản đã duyệt), người dùng xin thêm một trang, BA đáp bằng một câu
    // hỏi ⇒ cổng KHÔNG mở, dù bản Brief tồn tại.
    [Fact]
    public void Resolve_KeepsTheGateClosed_WhileTheLatestBaTurnIsAQuestion()
    {
        var t0 = new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc);
        var conversations = new[]
        {
            new AgentConversation { Role = "assistant", Message = "", ReadinessVerified = true, CreatedAt = t0 },
            new AgentConversation { Role = "user", Message = "thêm trang riêng để nhân viên xem JD được gán", CreatedAt = t0.AddMinutes(5) },
            new AgentConversation { Role = "assistant", Message = "Chỉ JD đang active, hay cả JD đã gán trước đây?", CreatedAt = t0.AddMinutes(6) },
        };
        var runs = new[]
        {
            new WorkflowRun
            {
                Name = WorkflowOrchestrator.RequirementDraftRunName,
                Status = WorkflowRunStatus.Completed,
                FinishedAt = t0.AddMinutes(1)
            }
        };

        var gate = WriteRequirementGate.Resolve(conversations, runs, briefExists: true, tablePending: false);

        Assert.False(gate.IsOpen);
        Assert.Equal(WriteRequirementGate.Waiting, gate.Value);
    }

    [Fact]
    public void FrameDone_OpensTheGateOnlyOnTheInvite()
    {
        var js = File.ReadAllText(FindRepoFile(Path.Combine("wwwroot", "js", "requirements.js")));

        Assert.Contains("writeReqWouldOpen = invited === true;", js);
        Assert.DoesNotContain("coverageReady", js);
    }

    // Frame done không còn chở cờ "bản đồ đã đủ": không có cờ thì client không có gì để mở cổng ngoài lời mời.
    [Fact]
    public void TurnResult_CarriesNoCoverageReadyFlag()
    {
        Assert.Null(typeof(BAChatTurnResult).GetProperty("CoverageReady"));
    }

    // wwwroot/ không được copy sang bin của test, nên đi ngược từ thư mục chạy lên tới repo root — cùng cách
    // PendingConfirmTableGateTests dò file.
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
}
