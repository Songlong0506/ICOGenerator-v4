namespace ICOGenerator.Services.Llm;

/// <summary>
/// Mang <c>prompt_cache_key</c> của lời gọi model ĐANG chạy xuống tới tầng HTTP.
/// <para>
/// Vì sao phải đi vòng qua <see cref="AsyncLocal{T}"/> thay vì truyền tham số: thứ dựng body thật là SDK
/// OpenAI ở downstream, và chỗ duy nhất còn sửa được body là
/// <see cref="LlmRequestCompatibilityHandler"/> — một <see cref="DelegatingHandler"/> chỉ nhìn thấy
/// <see cref="HttpRequestMessage"/>, không biết gì về project. Giá trị được đặt trong
/// <see cref="ModelCallLoggingChatClient"/> (nơi duy nhất cầm <see cref="ModelCallLogContext"/>) rồi chảy
/// xuống theo execution context của chính lời gọi đó, nên hai lời gọi song song — chat BA chạy ba nhánh
/// chuẩn bị ngữ cảnh trong ba DI scope riêng — không giẫm lên khóa của nhau.
/// </para>
/// <para>
/// <c>prompt_cache_key</c> chỉ là GỢI Ý ĐỊNH TUYẾN: request cùng khóa được đẩy về cùng backend nên khả
/// năng trúng prefix cache cao hơn. Nó KHÔNG phải khóa định danh cache (cache vẫn khớp theo prefix thật
/// của prompt), nên đặt sai chỉ làm giảm tỉ lệ trúng chứ không bao giờ khiến project này đọc được cache
/// của project khác.
/// </para>
/// </summary>
internal static class LlmCacheScope
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? CacheKey
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    /// <summary>Khóa định tuyến của một project. Tiền tố để không đụng namespace khóa của app khác dùng chung tài khoản.</summary>
    public static string KeyForProject(Guid projectId) => "ico-proj-" + projectId.ToString("N");
}
