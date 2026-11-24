using System.Security.Cryptography;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>Một link chia sẻ ở dạng trang quản lý hiển thị được (token đi kèm để dựng URL đầy đủ).</summary>
public record PocShareLinkItem(Guid Id, string Token, string? Label, DateTime ExpiresAtUtc, DateTime? RevokedAtUtc, string? CreatedBy, DateTime CreatedAt)
{
    public bool IsUsable => RevokedAtUtc == null && ExpiresAtUtc > DateTime.UtcNow;
}

public enum CreatePocShareLinkResult
{
    Ok,
    ProjectNotFound,

    /// <summary>Không nhập "Send to" — link không có chủ thì góp ý gửi qua nó không biết là của ai.</summary>
    MissingRecipient,

    /// <summary>Project đã chạm trần số link còn hiệu lực.</summary>
    TooManyLinks
}

/// <summary>
/// Tạo link chia sẻ bản demo cho người KHÔNG có tài khoản. Hạn dùng là bắt buộc và bị kẹp trần: một
/// link "sống mãi" là thứ không ai nhớ để thu hồi.
///
/// <para>
/// Người nhận ("Send to", lưu ở <see cref="PocShareLink.Label"/>) cũng bắt buộc: mỗi link là của MỘT
/// người, nên mọi góp ý gửi qua link đó được đứng tên người ấy — khách khỏi phải gõ tên mình ở mỗi lần
/// Add Note (xem <see cref="ResolvePocShareTokenQuery"/>).
/// </para>
/// </summary>
public class CreatePocShareLinkUseCase
{
    public const int DefaultDays = 14;
    public const int MaxDays = 90;

    /// <summary>Trần số link CÒN HIỆU LỰC mỗi project — đủ cho một buổi nghiệm thu, chặn việc rải link vô tội vạ.</summary>
    private const int MaxActiveLinks = 10;

    private readonly AppDbContext _db;

    public CreatePocShareLinkUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(CreatePocShareLinkResult Result, PocShareLinkItem? Item)> ExecuteAsync(
        Guid projectId, string? label, int days, string? createdBy, CancellationToken cancellationToken = default)
    {
        label = label?.Trim();
        if (string.IsNullOrEmpty(label))
            return (CreatePocShareLinkResult.MissingRecipient, null);

        if (!await _db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken))
            return (CreatePocShareLinkResult.ProjectNotFound, null);

        var now = DateTime.UtcNow;
        var activeCount = await _db.PocShareLinks
            .CountAsync(x => x.ProjectId == projectId && x.RevokedAtUtc == null && x.ExpiresAtUtc > now, cancellationToken);
        if (activeCount >= MaxActiveLinks)
            return (CreatePocShareLinkResult.TooManyLinks, null);

        var link = new PocShareLink
        {
            ProjectId = projectId,
            Token = GenerateToken(),
            Label = label[..Math.Min(label.Length, 200)],
            CreatedByUsername = createdBy,
            ExpiresAtUtc = now.AddDays(Math.Clamp(days <= 0 ? DefaultDays : days, 1, MaxDays))
        };

        _db.PocShareLinks.Add(link);
        await _db.SaveChangesAsync(cancellationToken);

        return (CreatePocShareLinkResult.Ok,
            new PocShareLinkItem(link.Id, link.Token, link.Label, link.ExpiresAtUtc, null, link.CreatedByUsername, link.CreatedAt));
    }

    // 32 byte entropy, base64url (không có ký tự cần escape trong URL). Đoán được token là vào được bản
    // demo, nên nguồn ngẫu nhiên phải là loại mật mã, không phải Random.
    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
}

/// <summary>Thu hồi một link chia sẻ (không xóa để còn dấu vết ai đã tạo link nào).</summary>
public class RevokePocShareLinkUseCase
{
    private readonly AppDbContext _db;

    public RevokePocShareLinkUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <param name="projectId">Project mà caller ĐÃ kiểm quyền — link phải thuộc đúng project này.</param>
    public async Task<bool> ExecuteAsync(Guid projectId, Guid linkId, CancellationToken cancellationToken = default)
    {
        var link = await _db.PocShareLinks.FirstOrDefaultAsync(x => x.Id == linkId && x.ProjectId == projectId, cancellationToken);
        if (link == null)
            return false;

        link.RevokedAtUtc ??= DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>Danh sách link chia sẻ của một project (mới nhất trước) cho panel quản lý ở trang POC Review.</summary>
public class ListPocShareLinksQuery
{
    private readonly AppDbContext _db;

    public ListPocShareLinksQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<PocShareLinkItem>> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await _db.PocShareLinks.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new PocShareLinkItem(x.Id, x.Token, x.Label, x.ExpiresAtUtc, x.RevokedAtUtc, x.CreatedByUsername, x.CreatedAt))
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Thứ một token hợp lệ mở ra: project được xem + người nhận link (<see cref="PocShareLink.Label"/>).
/// <paramref name="RecipientName"/> rỗng chỉ với link tạo từ trước khi "Send to" thành bắt buộc — khi đó
/// khách vẫn phải tự gõ tên lúc góp ý.
/// </summary>
public record PocShareAccess(Guid ProjectId, string? RecipientName);

/// <summary>
/// Đổi token trong URL của khách thành project được phép xem (kèm tên người nhận link). Trả <c>null</c> cho MỌI trường hợp không
/// hợp lệ (token lạ, hết hạn, đã thu hồi) — người lạ không phân biệt được "token sai" với "token đã hết
/// hạn", nên không dò được token nào từng tồn tại.
/// </summary>
public class ResolvePocShareTokenQuery
{
    private readonly AppDbContext _db;

    public ResolvePocShareTokenQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PocShareAccess?> ExecuteAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64)
            return null;

        var link = await _db.PocShareLinks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Token == token, cancellationToken);

        return link != null && link.IsUsable(DateTime.UtcNow)
            ? new PocShareAccess(link.ProjectId, string.IsNullOrWhiteSpace(link.Label) ? null : link.Label.Trim())
            : null;
    }
}
