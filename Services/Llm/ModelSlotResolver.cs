using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Tra model của một <see cref="ModelSlot"/> (màn AI Models → "Model theo vai trò"). Một chỗ duy nhất cho
/// mọi lời gọi phụ muốn chạy trên model của suất thay vì model của agent gọi nó.
///
/// <para>
/// <b>Quyền xin suất nằm ở điểm gọi.</b> Resolver không biết lời gọi nào được phép đi model của suất: lời
/// gọi nào gọi <see cref="FindAsync"/> là đã chọn, và lý do chọn (hay KHÔNG chọn) phải ghi ở đó, dựa trên
/// bằng chứng đo được (kiểm toán trọng tài). Ví dụ: <c>InterviewJudge</c> chỉ cho <c>BATurnSignalJudge</c>
/// xin suất <see cref="ModelSlot.Judge"/>; hai phán quyết có quyền xoá câu hỏi luôn chạy model của BA.
/// </para>
///
/// <para>
/// Suất chưa khai, hoặc model của suất đã bị tắt ở màn AI Models ⇒ <c>null</c>: caller chạy model của agent
/// gọi nó, không gọi một model mà quản trị viên đã chủ ý ngưng dùng.
/// </para>
/// </summary>
public class ModelSlotResolver
{
    private readonly AppDbContext _db;

    public ModelSlotResolver(AppDbContext db) => _db = db;

    /// <summary>Model đang gán cho <paramref name="slot"/> và còn bật; <c>null</c> = dùng model của agent gọi.</summary>
    public virtual async Task<AiModel?> FindAsync(ModelSlot slot, CancellationToken cancellationToken = default)
    {
        var model = await _db.ModelSlotAssignments
            .AsNoTracking()
            .Where(x => x.Slot == slot)
            .Select(x => x.AiModel)
            .FirstOrDefaultAsync(cancellationToken);

        return model is { IsActive: true } ? model : null;
    }
}
