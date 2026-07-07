using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

/// <summary>Xoá một lịch eval định kỳ. Run cũ sinh từ lịch vẫn giữ (ScheduleId là Guid không FK).</summary>
public class DeleteEvalScheduleUseCase
{
    private readonly AppDbContext _db;

    public DeleteEvalScheduleUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task ExecuteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var schedule = await _db.EvalSchedules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (schedule == null)
            return;

        _db.EvalSchedules.Remove(schedule);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
