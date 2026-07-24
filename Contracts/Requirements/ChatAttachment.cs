namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Một file người dùng đính kèm ở một lượt chat (lưu JSON trong <c>AgentConversation.Attachments</c>).
/// <paramref name="Id"/> trỏ về <c>ProjectSourceFile</c> — bubble hội thoại render ảnh qua endpoint
/// SourceContent theo id này; <paramref name="IsImage"/> quyết định render thumbnail hay chip tên file.
/// </summary>
public record ChatAttachment(Guid Id, string Name, bool IsImage);
