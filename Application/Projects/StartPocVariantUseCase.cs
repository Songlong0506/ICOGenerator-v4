using ICOGenerator.Configuration;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum StartPocVariantResult { Ok, ProjectNotFound, ModelNotFound, NoDeveloperAgent, NoSourcePoc, AlreadyRunning, LimitReached, HarnessDisabled }

/// <summary>
/// "Thử model khác" trên trang POC Review: xếp hàng một biến thể dựng lại bản demo bằng model được chọn
/// (xem <see cref="PocVariant"/>). Chỉ ghi một dòng Queued — <see cref="Services.Workflows.PocVariantWorker"/>
/// chạy nền, vì một lượt dựng POC kéo dài nhiều phút.
///
/// <para>
/// Hai giới hạn, đều vì đây là thí nghiệm tiêu tiền thật: mỗi dự án chỉ một lượt đang chờ/chạy (bấm đúp
/// hay bấm liền ba model không được thành ba lượt chồng nhau), và tối đa <see cref="MaxVariantsPerProject"/>
/// biến thể được giữ — muốn thử thêm thì xoá bớt kết quả cũ, để bảng so sánh vẫn đọc được bằng mắt.
/// Cho phép thử LẠI đúng model đang dùng: chạy hai lần cùng model là cách duy nhất biết chênh lệch giữa
/// các model có lớn hơn độ dao động của chính một model hay không.
/// </para>
///
/// <para>
/// Vỏ agent (<see cref="PocVariantHarness"/>) chọn cùng lúc với model. Vỏ opencode chỉ nhận khi
/// <see cref="PocOpencodeSettings.Enabled"/> — nó cần opencode cài trên máy chủ và cho một tiến trình ngoài
/// app chạy trong thư mục thử, nên người vận hành phải chủ động bật.
/// </para>
/// </summary>
public class StartPocVariantUseCase
{
    public const int MaxVariantsPerProject = 10;

    private readonly AppDbContext _db;
    private readonly PocOpencodeSettings _opencode;

    public StartPocVariantUseCase(AppDbContext db, PocOpencodeSettings opencode)
    {
        _db = db;
        _opencode = opencode;
    }

    public async Task<StartPocVariantResult> ExecuteAsync(Guid projectId, Guid aiModelId, string? requestedBy,
        PocVariantHarness harness = PocVariantHarness.Builtin, CancellationToken cancellationToken = default)
    {
        if (harness == PocVariantHarness.Opencode && !_opencode.Enabled)
            return StartPocVariantResult.HarnessDisabled;

        if (!await _db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken))
            return StartPocVariantResult.ProjectNotFound;

        var model = await _db.AiModels.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == aiModelId && m.IsActive, cancellationToken);
        if (model == null)
            return StartPocVariantResult.ModelNotFound;

        if (!await _db.Agents.AnyAsync(a => a.RoleKey == AgentRoleKey.Developer, cancellationToken))
            return StartPocVariantResult.NoDeveloperAgent;

        var source = await PocVariantSource.FindAsync(_db, projectId, cancellationToken);
        if (source == null || string.IsNullOrWhiteSpace(source.Input))
            return StartPocVariantResult.NoSourcePoc;

        var variants = _db.PocVariants.Where(v => v.ProjectId == projectId);
        if (await variants.AnyAsync(v => v.Status == PocVariantStatus.Queued || v.Status == PocVariantStatus.Running, cancellationToken))
            return StartPocVariantResult.AlreadyRunning;
        if (await variants.CountAsync(cancellationToken) >= MaxVariantsPerProject)
            return StartPocVariantResult.LimitReached;

        _db.PocVariants.Add(new PocVariant
        {
            ProjectId = projectId,
            AiModelId = model.Id,
            ModelLabel = model.ModelId,
            Harness = harness,
            SourceTaskId = source.Id,
            RequestedBy = requestedBy
        });
        await _db.SaveChangesAsync(cancellationToken);
        return StartPocVariantResult.Ok;
    }
}
