using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Application.Requirements;

public class ChatWithBAUseCase
{
    private readonly BARequirementService _baRequirementService;

    public ChatWithBAUseCase(BARequirementService baRequirementService)
    {
        _baRequirementService = baRequirementService;
    }

    /// <param name="onStatus">Callback trạng thái ngắn cho UI streaming (null khi gọi kiểu postback cổ điển).</param>
    /// <param name="onToken">Callback nhận text hiển thị được khi BA "đang gõ" (null = không stream).</param>
    public Task<BAChatTurnResult> ExecuteAsync(Guid projectId, string message,
        Action<string>? onStatus = null, Action<string>? onToken = null, CancellationToken cancellationToken = default) =>
        _baRequirementService.ChatAsync(projectId, message, onStatus, onToken, cancellationToken);
}
