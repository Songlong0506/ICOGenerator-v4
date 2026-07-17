using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Application.Requirements;

public class ChatWithBAUseCase
{
    private readonly BAChatService _baChatService;

    public ChatWithBAUseCase(BAChatService baChatService)
    {
        _baChatService = baChatService;
    }

    /// <param name="onStatus">Callback trạng thái ngắn cho UI streaming (null khi gọi kiểu postback cổ điển).</param>
    /// <param name="onToken">Callback nhận text hiển thị được khi BA "đang gõ" (null = không stream).</param>
    public Task<BAChatTurnResult> ExecuteAsync(Guid projectId, string message,
        Action<string>? onStatus = null, Action<string>? onToken = null, CancellationToken cancellationToken = default) =>
        _baChatService.ChatAsync(projectId, message, onStatus, onToken, cancellationToken);
}
