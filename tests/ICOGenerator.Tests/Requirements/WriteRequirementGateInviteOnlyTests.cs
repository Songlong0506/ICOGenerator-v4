using ICOGenerator.Contracts.Requirements;
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
    [Fact]
    public void ServerRender_OpensTheGateOnlyOnTheInvite()
    {
        var view = File.ReadAllText(FindRepoFile(Path.Combine("Views", "Requirements", "Index.cshtml")));

        Assert.Contains(": requirementReady ? \"ready\"", view);
        Assert.DoesNotContain("coverageReady", view);
        Assert.DoesNotContain("data-draft-exists", view);
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
