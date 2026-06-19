using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

// Backs the "＋ New Chat" button (previously a no-op redirect): clears the project's BA
// conversation history so the next chat starts fresh.
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
