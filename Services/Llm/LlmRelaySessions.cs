using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ICOGenerator.Services.Llm;

/// <summary>Một lượt được phép gọi model qua relay: tiền tính cho dự án nào, nhân danh agent nào, model nào, nhãn Purpose gì.</summary>
public sealed record LlmRelaySession(Guid ProjectId, Guid AgentId, Guid AiModelId, string Purpose, Guid? WorkflowRunId = null);

/// <summary>
/// Token tạm cho relay OpenAI-compatible (<c>/opencode-relay/v1</c>) — singleton, chỉ trong bộ nhớ.
///
/// <para>
/// Agent CLI bên ngoài (opencode) không bao giờ cầm API key thật của model: nó chỉ cầm một token ngẫu
/// nhiên sống đúng bằng một lượt chạy, và token đó chỉ mở được đúng MỘT model cho đúng MỘT dự án. Restart
/// app là mất hết token — đúng ý: lượt đang chạy dở cũng đã bị worker đánh Failed lúc khởi động lại.
/// </para>
/// </summary>
public sealed class LlmRelaySessions
{
    private readonly ConcurrentDictionary<string, LlmRelaySession> _sessions = new(StringComparer.Ordinal);

    public string Open(LlmRelaySession session)
    {
        var token = "icorelay_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _sessions[token] = session;
        return token;
    }

    public LlmRelaySession? Resolve(string? token) =>
        !string.IsNullOrEmpty(token) && _sessions.TryGetValue(token, out var session) ? session : null;

    public void Close(string token) => _sessions.TryRemove(token, out _);
}
