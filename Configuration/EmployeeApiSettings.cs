namespace ICOGenerator.Configuration;

/// <summary>
/// Cấu hình đồng bộ OrgUnits/Associates từ employee-api (bind từ section "EmployeeApi"). Job chạy bằng
/// Hangfire theo <see cref="SyncCron"/> — xem <c>Services/Organization/OrganizationSyncJob</c>.
/// </summary>
public class EmployeeApiSettings
{
    public const string SectionName = "EmployeeApi";

    /// <summary>
    /// Bật job đồng bộ. Mặc định của CODE là false: thiếu cả section thì không lên lịch gì, thay vì để
    /// job thất bại mỗi ngày vì không có URL/secret. <c>appsettings.json</c> bật sẵn.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>URL gốc employee-api; các path bên dưới tương đối với nó.</summary>
    public string BaseURL { get; set; } = string.Empty;

    /// <summary>Danh sách toàn bộ nhân sự đang làm việc.</summary>
    public string AllAssociatesPath { get; set; } = string.Empty;

    /// <summary>Thông tin nghỉ việc của MỘT người; <c>{0}</c> là chỗ chèn PersonalNumber.</summary>
    public string LeavingInfoPath { get; set; } = string.Empty;

    /// <summary>Danh sách toàn bộ orgUnit.</summary>
    public string OrgUnitsPath { get; set; } = string.Empty;

    /// <summary>Lịch chạy (cron 5 trường) theo múi giờ <see cref="SyncTimeZone"/>. Mặc định 11:00 mỗi ngày.</summary>
    public string SyncCron { get; set; } = "0 11 * * *";

    /// <summary>
    /// Múi giờ của <see cref="SyncCron"/>. Nhận ID IANA (<c>Asia/Ho_Chi_Minh</c>) lẫn ID Windows
    /// (<c>SE Asia Standard Time</c>) — tự quy đổi khi hệ điều hành chỉ biết một loại.
    /// </summary>
    public string SyncTimeZone { get; set; } = "Asia/Ho_Chi_Minh";

    /// <summary>Lấy access token (OAuth2 client_credentials) để gọi employee-api.</summary>
    public EmployeeApiClientCredentialSettings ClientCredential { get; set; } = new();
}

/// <summary>Client đăng ký ở IdentityServer cho lời gọi máy-tới-máy tới employee-api.</summary>
public class EmployeeApiClientCredentialSettings
{
    /// <summary>URL tuyệt đối của token endpoint.</summary>
    public string TokenUrl { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    /// <summary>KHÔNG commit secret thật — nạp qua biến môi trường <c>EmployeeApi__ClientCredential__ClientSecret</c>.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Scope xin kèm token. Trống ⇒ không gửi tham số scope.</summary>
    public string Scope { get; set; } = string.Empty;
}
