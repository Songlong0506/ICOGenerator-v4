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

    // ==== Cửa sổ nén (BriefContextWindow) ====

    [Fact]
    public void BuildWindowed_CutsHeadOfConversation_AndReportsSkippedCount()
    {
        var turns = new[]
        {
            Turn("user", "Ý cũ 1", 1),
            Turn("assistant", "Câu hỏi cũ", 2),
            Turn("user", "Ý cũ 2", 3),
            Turn("assistant", "Câu hỏi mới", 4),
            Turn("user", "Ý mới", 5)
        };

        // Mốc duyệt = 3 lượt đầu, và cả 3 đều đã nằm trong tóm tắt ⇒ được cắt.
        var transcript = ConversationTranscriptBuilder.BuildWindowed(turns, summarizedTurnCount: 3, approvedTurnCount: 3);

        Assert.Equal(3, transcript.SkippedTurns);
        Assert.Equal(
            "BA: Câu hỏi mới\n" +
            "Người dùng: Ý mới",
            transcript.Text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void BuildWindowed_CountsFilteredTurns_SoPointersStayAligned()
    {
        // Con trỏ bộ nhớ đếm MỌI dòng hội thoại, kể cả lượt rỗng và lượt báo lỗi gọi AI (những lượt bị lọc
        // khỏi transcript). Đếm lệch một dòng là cắt nhầm sang lượt chưa được tóm tắt — mất thông tin âm thầm.
        var turns = new[]
        {
            Turn("user", "Ý cũ", 1),
            Turn("assistant", ConversationTranscriptBuilder.LlmFailurePrefix + ": timeout", 2),
            Turn("user", "   ", 3),
            Turn("assistant", "Câu hỏi mới", 4),
            Turn("user", "Ý mới", 5)
        };

        var transcript = ConversationTranscriptBuilder.BuildWindowed(turns, summarizedTurnCount: 3, approvedTurnCount: 3);

        Assert.Equal(3, transcript.SkippedTurns);
        Assert.Equal(
            "BA: Câu hỏi mới\n" +
            "Người dùng: Ý mới",
            transcript.Text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void BuildWindowed_WithoutSummary_SendsWholeConversation()
    {
        // Chưa tóm tắt lượt nào (dự án ngắn, hoặc lời gọi tóm tắt lỗi) ⇒ hành vi y hệt Build cũ.
        var turns = new[] { Turn("user", "Ý 1", 1), Turn("assistant", "Hỏi", 2), Turn("user", "Ý 2", 3) };

        var transcript = ConversationTranscriptBuilder.BuildWindowed(turns, summarizedTurnCount: 0, approvedTurnCount: 3);

        Assert.Equal(0, transcript.SkippedTurns);
        Assert.Equal(ConversationTranscriptBuilder.Build(turns), transcript.Text);
    }

    [Fact]
    public void BuildWindowed_NoUserTurns_ReturnsPlaceholder()
    {
        var transcript = ConversationTranscriptBuilder.BuildWindowed(
            new[] { Turn("assistant", "Bạn muốn xây ứng dụng gì?", 1) }, summarizedTurnCount: 1, approvedTurnCount: 1);

        Assert.Equal(ConversationTranscriptBuilder.NoRequirementPlaceholder, transcript.Text);
        Assert.Equal(0, transcript.SkippedTurns);
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
