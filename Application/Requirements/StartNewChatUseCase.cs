using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

// Backs the "＋ New Chat" button. It used to post to an action that only redirected (a no-op
// button), so this clears the project's BA conversation history so the next chat starts fresh.
public class StartNewChatUseCase
{
    private readonly AppDbContext _db;

    public StartNewChatUseCase(AppDbContext db)
    {
        _db = db;
    }

    public Task ExecuteAsync(Guid projectId) =>
        _db.AgentConversations
            .Where(c => c.ProjectId == projectId)
            .ExecuteDeleteAsync();
}
