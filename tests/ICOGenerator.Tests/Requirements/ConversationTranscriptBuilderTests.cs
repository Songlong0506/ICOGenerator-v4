using System.Text.Json;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Transcript Hỏi–Đáp cho readiness gate + lượt soạn Product Brief. Các test chốt: (1) giữ CẢ câu hỏi của
// BA lẫn câu trả lời của user theo đúng thứ tự thời gian (đây là lý do tồn tại — bản cũ chỉ lấy lượt user,
// câu trả lời chip ngắn mất sạch ngữ cảnh); (2) chưa có lượt user nào thì trả placeholder; (3) lượt BA báo
// lỗi gọi AI và lượt rỗng bị lọc bỏ; (4) lượt BA có gợi ý thì đính kèm option để đáp án tham chiếu còn ngữ cảnh.
public class ConversationTranscriptBuilderTests
{
    private static AgentConversation Turn(string role, string message, int second, string? suggestions = null) => new()
    {
        Role = role,
        Message = message,
        Suggestions = suggestions,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, second, DateTimeKind.Utc)
    };

    [Fact]
    public void Build_KeepsQuestionAndAnswerPairs_InChronologicalOrder()
    {
        var transcript = ConversationTranscriptBuilder.Build(new[]
        {
            // Cố tình đưa vào lệch thứ tự để chốt việc sắp theo CreatedAt.
            Turn("user", "Nhân viên văn phòng", 3),
            Turn("user", "Tôi muốn app quản lý đơn nghỉ phép", 1),
            Turn("assistant", "Đối tượng người dùng chính là ai?", 2)
        });

        Assert.Equal(
            "Người dùng: Tôi muốn app quản lý đơn nghỉ phép\n" +
            "BA: Đối tượng người dùng chính là ai?\n" +
            "Người dùng: Nhân viên văn phòng",
            transcript.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Build_NoUserTurns_ReturnsPlaceholder()
    {
        Assert.Equal("(Chưa có yêu cầu nào được ghi nhận.)",
            ConversationTranscriptBuilder.Build(new[] { Turn("assistant", "Bạn muốn xây ứng dụng gì?", 1) }));

        Assert.Equal("(Chưa có yêu cầu nào được ghi nhận.)",
            ConversationTranscriptBuilder.Build(Array.Empty<AgentConversation>()));
    }

    [Fact]
    public void Build_AttachesBaSuggestions_SoReferentialAnswerKeepsContext()
    {
        var suggestions = JsonSerializer.Serialize(new[]
        {
            "Số hóa quy trình thủ công trên Excel",
            "Chuẩn hóa mẫu JD và quản lý phiên bản",
            "Cả hai mục tiêu trên"
        });

        var transcript = ConversationTranscriptBuilder.Build(new[]
        {
            Turn("assistant", "Mục tiêu cụ thể của ứng dụng này là gì?", 1, suggestions),
            Turn("user", "Cả hai mục tiêu trên", 2)
        }).Replace("\r\n", "\n");

        Assert.Equal(
            "BA: Mục tiêu cụ thể của ứng dụng này là gì?\n" +
            "   (Các lựa chọn gợi ý đã đưa cho người dùng: " +
            "[1] Số hóa quy trình thủ công trên Excel; " +
            "[2] Chuẩn hóa mẫu JD và quản lý phiên bản; " +
            "[3] Cả hai mục tiêu trên)\n" +
            "Người dùng: Cả hai mục tiêu trên",
            transcript);
    }

    [Fact]
    public void Build_FiltersLlmFailureTurns_AndBlankMessages()
    {
        var transcript = ConversationTranscriptBuilder.Build(new[]
        {
            Turn("user", "Quản lý kho", 1),
            Turn("assistant", ConversationTranscriptBuilder.LlmFailurePrefix + ", chưa thể trả lời. Chi tiết: timeout", 2),
            Turn("assistant", "   ", 3),
            Turn("user", "", 4),
            Turn("assistant", "Kho của anh/chị chứa mặt hàng gì?", 5)
        });

        Assert.Equal(
            "Người dùng: Quản lý kho\n" +
            "BA: Kho của anh/chị chứa mặt hàng gì?",
            transcript.Replace("\r\n", "\n"));
    }
}
