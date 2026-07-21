using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Render dùng chung một lượt hội thoại cho các ngữ cảnh gửi LLM. Chốt: (1) lượt user render text thuần;
// (2) lượt BA có gợi ý thì đính kèm danh sách option đã đưa ra (đánh số) — đây là lý do tồn tại: câu trả
// lời tham chiếu "Cả hai mục tiêu trên" phải nối được về option; (3) lượt BA không/hỏng gợi ý render như
// cũ; (4) ParseSuggestions an toàn với null/rỗng/JSON hỏng.
public class ConversationTurnRendererTests
{
    private static AgentConversation Turn(string role, string message, string? suggestions = null) =>
        new() { Role = role, Message = message, Suggestions = suggestions };

    [Fact]
    public void Render_UserTurn_IsPlainText()
    {
        Assert.Equal("Người dùng: Cả hai mục tiêu trên",
            ConversationTurnRenderer.Render(Turn("user", "Cả hai mục tiêu trên")));
    }

    [Fact]
    public void Render_AssistantTurnWithSuggestions_AppendsNumberedOptions()
    {
        var suggestions = JsonSerializer.Serialize(new[]
        {
            "Số hóa quy trình thủ công trên Excel",
            "Chuẩn hóa mẫu JD và quản lý phiên bản",
            "Cả hai mục tiêu trên"
        });

        var rendered = ConversationTurnRenderer.Render(
            Turn("assistant", "Mục tiêu cụ thể của ứng dụng này là gì?", suggestions));

        Assert.Equal(
            "BA: Mục tiêu cụ thể của ứng dụng này là gì?\n" +
            "   (Các lựa chọn gợi ý đã đưa cho người dùng: " +
            "[1] Số hóa quy trình thủ công trên Excel; " +
            "[2] Chuẩn hóa mẫu JD và quản lý phiên bản; " +
            "[3] Cả hai mục tiêu trên)",
            rendered.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ khong-phai-json-hop-le")]
    public void Render_AssistantTurnWithoutUsableSuggestions_IsPlainText(string? suggestions)
    {
        Assert.Equal("BA: Đối tượng người dùng chính là ai?",
            ConversationTurnRenderer.Render(Turn("assistant", "Đối tượng người dùng chính là ai?", suggestions)));
    }

    [Fact]
    public void Render_AssistantTurnWithFlowDiagram_AppendsConfirmedFlowSteps()
    {
        var flow = JsonSerializer.Serialize(new[]
        {
            new FlowStep { Actor = "Nhân viên", Action = "Gửi đơn nghỉ phép", Outcome = "Đơn ở trạng thái Chờ duyệt" },
            new FlowStep { Actor = "Quản lý", Action = "Duyệt đơn", Outcome = "Đơn Đã duyệt, khóa sửa" },
            new FlowStep { Actor = "", Action = "Hệ thống gửi thông báo", Outcome = "" }
        });
        var turn = Turn("assistant", "Nếu tóm tắt đã đủ, bấm \"Write Requirement\" nhé.");
        turn.FlowDiagram = flow;

        var rendered = ConversationTurnRenderer.Render(turn).Replace("\r\n", "\n");

        Assert.Equal(
            "BA: Nếu tóm tắt đã đủ, bấm \"Write Requirement\" nhé.\n" +
            "   (Sơ đồ luồng nghiệp vụ đã trình bày cho người dùng xác nhận: " +
            "1. Nhân viên: Gửi đơn nghỉ phép → Đơn ở trạng thái Chờ duyệt; " +
            "2. Quản lý: Duyệt đơn → Đơn Đã duyệt, khóa sửa; " +
            "3. Hệ thống gửi thông báo)",
            rendered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ khong-phai-json")]
    public void Render_AssistantTurnWithoutUsableFlowDiagram_IsPlainText(string? flowDiagram)
    {
        var turn = Turn("assistant", "Đối tượng người dùng chính là ai?");
        turn.FlowDiagram = flowDiagram;
        Assert.Equal("BA: Đối tượng người dùng chính là ai?", ConversationTurnRenderer.Render(turn));
    }

    [Fact]
    public void ParseFlowDiagram_DropsStepsWithoutAction_AndSurvivesMalformedJson()
    {
        var flow = JsonSerializer.Serialize(new[]
        {
            new FlowStep { Actor = "Nhân viên", Action = "  ", Outcome = "..." },
            new FlowStep { Actor = "Quản lý", Action = "Duyệt đơn", Outcome = "" }
        });

        var parsed = ConversationTurnRenderer.ParseFlowDiagram(flow);
        Assert.Single(parsed);
        Assert.Equal("Duyệt đơn", parsed[0].Action);

        Assert.Empty(ConversationTurnRenderer.ParseFlowDiagram("khong-phai-json"));
        Assert.Empty(ConversationTurnRenderer.ParseFlowDiagram(null));
    }

    [Fact]
    public void ParseSuggestions_MalformedOrEmpty_ReturnsEmptyList()
    {
        Assert.Empty(ConversationTurnRenderer.ParseSuggestions(null));
        Assert.Empty(ConversationTurnRenderer.ParseSuggestions("   "));
        Assert.Empty(ConversationTurnRenderer.ParseSuggestions("khong-phai-json"));
        Assert.Equal(new[] { "A", "B" }, ConversationTurnRenderer.ParseSuggestions("[\"A\",\"B\"]"));
    }
}
