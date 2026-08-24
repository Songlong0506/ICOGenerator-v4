using System.Text.RegularExpressions;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

// AI LÀ NGƯỜI ĐƯỢC PHÉP KHỞI ĐỘNG VÒNG SOẠN TÀI LIỆU — và vì sao câu trả lời không bao giờ được là
// "một lượt chat".
//
// Vòng soạn Product Brief là lượt đắt nhất phía yêu cầu (soạn + tự soát + sửa, mỗi bước một lời gọi LLM
// dài). Nó chỉ được chạy khi người dùng ra một LỆNH TƯỜNG MINH: bấm nút "Write Requirement", gửi ghi chú
// đã ghim trên bản xem trước, hoặc gửi phản hồi POC về phía yêu cầu. Cả ba đều là một cú submit có chủ ý,
// nói đúng một điều: "lấy những gì đang có mà viết".
//
// Cám dỗ thường trực là nối nó vào lượt chat — "người dùng vừa trả lời xong câu hỏi của cổng thì tự viết
// tiếp cho họ, đỡ phải bấm". Đó là chỗ luật này tồn tại để chặn:
//   • Một câu trong khung chat KHÔNG phải lệnh viết tài liệu. Người dùng trả lời câu hỏi rồi định kể thêm
//     ba ý nữa là chuyện thường; tự chạy ở câu đầu tiên là cướp lượt và đốt token cho một bản draft đọc
//     thiếu đúng ba ý đó.
//   • Bản đồ bao phủ do LLM chắt nên nó NHẤP NHÁY: một lượt distill lỡ nâng đủ dòng lên [RÕ] là một run
//     tự bay, không ai bấm gì cả.
//   • requirement-chat.v4.md CẤM BA hứa một bước chạy ngầm giữa hai lượt, đúng vì ở chế độ này không có
//     bước nào như thế. Nối vòng soạn vào lượt chat là biến điều prompt đang dạy BA thành lời nói dối.
//
// Đổi lại, mọi đường bị chặn giữa chừng (RequirementDraftOutcome.NeedsMoreInfo) phải nói THẲNG rằng người
// dùng còn phải bấm nút lần nữa — xem AgentTaskWorker.RunRequirementDraftAsync.
public class RequirementDraftTriggerCoverageTests
{
    // Mỗi dòng là một LỆNH TƯỜNG MINH của người dùng. Thêm vào đây phải kèm lý do, và lý do phải trả lời
    // được: cú bấm/cú gửi nào của người dùng đứng sau nó?
    private static readonly Dictionary<string, string> AllowedCallers = new()
    {
        ["Controllers/RequirementsController.cs"] =
            "Nút \"Write Requirement\" — cú bấm trực tiếp, và là đường duy nhất của màn hình Requirements.",
        ["Application/Requirements/ReviseBriefFromNotesUseCase.cs"] =
            "Người dùng gửi các ghi chú đã ghim trên bản xem trước Product Brief: một cú submit nói rõ " +
            "\"sửa tài liệu theo mấy chỗ này\".",
        ["Application/Requirements/RoutePocFeedbackToRequirementUseCase.cs"] =
            "Phản hồi POC được người duyệt chuyển về phía yêu cầu — cũng là một cú submit có chủ ý.",
        // Bản thân use case và chỗ đăng ký DI của nó.
        ["Application/Requirements/GenerateRequirementDraftUseCase.cs"] = "Chính nó.",
        ["Extensions/ApplicationServiceCollectionExtensions.cs"] = "Đăng ký DI.",
    };

    [Fact]
    public void RequirementDraftWorkflow_IsStartedOnlyByExplicitUserCommands()
    {
        var root = RepoRoot();
        var callers = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsGenerated(root, f))
            .Where(f => StripComments(File.ReadAllText(f)).Contains("GenerateRequirementDraftUseCase", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var unexpected = callers.Where(c => !AllowedCallers.ContainsKey(c)).ToList();

        Assert.True(unexpected.Count == 0,
            "Có đường mới khởi động vòng soạn Product Brief: " + string.Join(", ", unexpected)
            + ". Nếu đó là một LỆNH TƯỜNG MINH của người dùng thì khai báo vào AllowedCallers kèm lý do; "
            + "nếu nó chạy theo một lượt chat thì đọc lại chú thích đầu file trước đã.");
    }

    // Soi CODE, không soi chú thích: các file giải thích luật này (như AgentTaskWorker) nhắc tên use case
    // trong comment mà không hề gọi nó — tính chúng là caller thì test tự biến tài liệu thành vi phạm.
    private static string StripComments(string source)
    {
        var noBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlocks, @"//[^\n]*", string.Empty);
    }

    private static bool IsGenerated(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        return rel.StartsWith("tests/", StringComparison.Ordinal)
               || rel.StartsWith("Migrations/", StringComparison.Ordinal)
               || rel.Contains("/obj/", StringComparison.Ordinal)
               || rel.Contains("/bin/", StringComparison.Ordinal);
    }

    // Đi ngược lên tới thư mục có ICOGenerator.csproj — cùng cách PromptConventionTests tìm Prompts/.
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ICOGenerator.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Không tìm thấy repo root từ " + AppContext.BaseDirectory);
    }
}
