using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.AspNetCore.Http;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Ghi audit log dùng AppDbContext (scoped) và IHttpContextAccessor để lấy người thực hiện. Tách thành một
/// service cross-cutting để các use case chỉ cần gọi một dòng, không phải tự lấy actor hay tự che secret.
/// Mọi lỗi ghi log đều được nuốt + log qua ILogger: audit log là phụ trợ debug, không được làm hỏng thao
/// tác cấu hình vừa lưu thành công.
/// </summary>
public class AuditLogger : IAuditLogger
{
    // Trường có TÊN chứa một trong các chuỗi này bị che thành "***" (so khớp không phân biệt hoa thường) để
    // secret (API key, connection string, mật khẩu...) không lọt vào BeforeJson/AfterJson — kể cả khi một
    // call-site mới quên tự che. Đây là lưới an toàn cuối cùng.
    private static readonly string[] SensitiveKeyParts =
        { "apikey", "password", "secret", "token", "connectionstring" };

    private const string RedactedValue = "***";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(AppDbContext db, IHttpContextAccessor httpContextAccessor, ILogger<AuditLogger> logger)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task LogAsync(
        AuditCategory category,
        AuditAction action,
        string entityId,
        string summary,
        object? before = null,
        object? after = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var user = _httpContextAccessor.HttpContext?.User;

            var entry = new AuditLog
            {
                Category = category,
                Action = action,
                EntityId = entityId,
                Summary = summary,
                ActorUsername = string.IsNullOrWhiteSpace(user?.Identity?.Name) ? "system" : user!.Identity!.Name!,
                ActorRole = user?.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
                BeforeJson = Serialize(before),
                AfterJson = Serialize(after)
            };

            _db.AuditLogs.Add(entry);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Không ném ra ngoài: thao tác cấu hình đã được lưu trước đó, mất một dòng audit không nên làm
            // người dùng thấy lỗi. Ghi lại để còn truy được vì sao audit không ghi được.
            _logger.LogError(ex, "Ghi audit log thất bại cho {Category}/{Action} {EntityId}", category, action, entityId);
        }
    }

    private static string? Serialize(object? value)
    {
        if (value is null)
            return null;

        var node = JsonSerializer.SerializeToNode(value, JsonOptions);
        if (node is null)
            return null;

        Redact(node);
        return node.ToJsonString(JsonOptions);
    }

    // Che tại chỗ các giá trị có tên trường nhạy cảm, đệ quy qua object/array lồng nhau.
    private static void Redact(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is null)
                        continue;

                    if (IsSensitive(key) && child is JsonValue)
                        obj[key] = RedactedValue;
                    else
                        Redact(child);
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                    if (item is not null)
                        Redact(item);
                break;
        }
    }

    private static bool IsSensitive(string key)
    {
        foreach (var part in SensitiveKeyParts)
            if (key.Contains(part, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
