using ICOGenerator.Services.Security;

namespace ICOGenerator.Tests;

/// <summary>
/// <see cref="IApiKeyProtector"/> không mã hóa gì — dùng cho mọi test dựng <c>AppDbContext</c> trên Sqlite.
/// <para>
/// AppDbContext bắt buộc nhận một protector (value converter của cột ApiKey), nhưng gần như không test nào
/// quan tâm tới việc mã hóa; chúng chỉ cần một hiện thực đi qua. Trước đây 84 file test mỗi file chép lại
/// đúng class này dưới dạng <c>private sealed</c> — và một bản đã trôi lệch chữ ký (nhận <c>string</c> thay
/// vì <c>string?</c>), đủ để build sinh cảnh báo CS8767. Để ở namespace gốc <c>ICOGenerator.Tests</c> nên
/// các namespace con (<c>ICOGenerator.Tests.Requirements</c>…) thấy được mà không cần thêm <c>using</c>.
/// </para>
/// </summary>
internal sealed class PassthroughApiKeyProtector : IApiKeyProtector
{
    public string Protect(string? plainText) => plainText ?? string.Empty;
    public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
}
