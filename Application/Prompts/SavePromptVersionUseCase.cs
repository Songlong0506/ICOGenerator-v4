using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Prompts;

/// <summary>
/// Lưu nội dung sửa của một template thành PHIÊN BẢN MỚI và kích hoạt ngay (bản mới thay nội dung
/// file trên mọi lời gọi LLM kế tiếp). Lần sửa ĐẦU TIÊN của một template chụp thêm nội dung file
/// hiện hành làm v1 (baseline, không active) để lịch sử luôn diff được về bản gốc. Nội dung trùng
/// khít bản đang dùng thì KHÔNG snapshot (cùng tinh thần ProjectDocumentRevision).
/// </summary>
public class SavePromptVersionUseCase
{
    private readonly AppDbContext _db;
    private readonly PromptFileCatalog _catalog;
    private readonly PromptTemplateService _templates;
    private readonly IPromptOverrideProvider _overrides;
    private readonly IAuditLogger _audit;

    public SavePromptVersionUseCase(
        AppDbContext db,
        PromptFileCatalog catalog,
        PromptTemplateService templates,
        IPromptOverrideProvider overrides,
        IAuditLogger audit)
    {
        _db = db;
        _catalog = catalog;
        _templates = templates;
        _overrides = overrides;
        _audit = audit;
    }

    public async Task<SavePromptVersionResult> ExecuteAsync(
        string? promptKey, string? content, string? changeNote, string? username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(promptKey) || string.IsNullOrWhiteSpace(content))
            return SavePromptVersionResult.InvalidInput;

        var key = promptKey.Trim();
        // Textarea của form gửi CRLF — chuẩn hoá về LF để so trùng/diff với nội dung file (LF) không lệch.
        var normalized = content.Replace("\r\n", "\n");

        var versions = await _db.PromptTemplateVersions
            .Where(v => v.PromptKey == key)
            .ToListAsync(cancellationToken);

        var fileContent = TryGetFileContent(key);
        if (versions.Count == 0 && fileContent == null)
            return SavePromptVersionResult.UnknownPromptKey;

        var currentActive = versions.FirstOrDefault(v => v.IsActive)?.Content ?? fileContent;
        if (string.Equals(normalized, currentActive, StringComparison.Ordinal))
            return SavePromptVersionResult.NoChange;

        // Lần sửa đầu tiên: chụp bản gốc từ file làm v1 để "prompt đã từng là gì" không mất và mọi
        // phiên bản sau đều diff được về gốc.
        var nextNumber = versions.Count == 0 ? 1 : versions.Max(v => v.VersionNumber) + 1;
        if (versions.Count == 0 && fileContent != null)
        {
            _db.PromptTemplateVersions.Add(new PromptTemplateVersion
            {
                PromptKey = key,
                VersionNumber = nextNumber++,
                Content = fileContent,
                ChangeNote = "Bản gốc chụp từ file",
                IsActive = false,
                CreatedByUsername = username
            });
        }

        foreach (var version in versions.Where(v => v.IsActive))
            version.IsActive = false;

        _db.PromptTemplateVersions.Add(new PromptTemplateVersion
        {
            PromptKey = key,
            VersionNumber = nextNumber,
            Content = normalized,
            ChangeNote = string.IsNullOrWhiteSpace(changeNote) ? null : changeNote.Trim(),
            IsActive = true,
            CreatedByUsername = username
        });

        await _db.SaveChangesAsync(cancellationToken);
        // Cache provider phải sạch NGAY để lời gọi LLM kế tiếp dùng bản mới (không đợi hết TTL 30s).
        _overrides.Invalidate();

        await _audit.LogAsync(AuditCategory.Prompt, AuditAction.Update, key,
            $"Lưu & kích hoạt phiên bản v{nextNumber} cho prompt \"{key}\"",
            after: new { VersionNumber = nextNumber, ChangeNote = changeNote }, cancellationToken: cancellationToken);

        return SavePromptVersionResult.Saved;
    }

    // File có thể không còn (template mồ côi chỉ sống bằng phiên bản DB) — coi như không có baseline.
    private string? TryGetFileContent(string key)
    {
        if (!_catalog.Exists(key))
            return null;
        try
        {
            return _templates.GetFileContent(key);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
