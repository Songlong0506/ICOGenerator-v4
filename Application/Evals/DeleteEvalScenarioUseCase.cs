using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

/// <summary>
/// Xoá hẳn một scenario. Kết quả các run cũ vẫn còn (EvalResult tham chiếu scenario bằng Guid + snapshot
/// tên, không FK) nên lịch sử điểm không mất; chỉ là run mới không chạy scenario này nữa.
/// </summary>
public class DeleteEvalScenarioUseCase
{
    private readonly AppDbContext _db;

    public DeleteEvalScenarioUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task ExecuteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var scenario = await _db.EvalScenarios.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (scenario == null)
            return;

        _db.EvalScenarios.Remove(scenario);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
