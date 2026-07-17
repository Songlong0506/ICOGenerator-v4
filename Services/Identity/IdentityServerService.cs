using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace ICOGenerator.Services.Identity;

/// <summary>
/// Cầu nối gọi REST API quản trị của Bosch IdentityServer (IS4): lấy danh sách role của API resource,
/// tra cứu người dùng LDAP, liệt kê user theo role, và GÁN / THU HỒI role cho user. Bearer token lấy từ
/// <c>access_token</c> của phiên SSO hiện tại (OIDC SaveTokens=true) nên chức năng này chỉ hoạt động khi
/// Authentication:Provider = "IdentityServer"; ở chế độ Local (không có access_token) lời gọi sẽ trả rỗng.
///
/// Đường dẫn endpoint + APIName + BaseURL đọc từ section "IdentityServer" (giống mẫu Bosch). IS4 nội bộ
/// dùng chứng chỉ tự ký nên HttpClient (đăng ký ở <see cref="HttpClientName"/>) bỏ qua kiểm tra chứng chỉ.
/// </summary>
public class IdentityServerService
{
    /// <summary>Tên named-HttpClient (đăng ký ở ApplicationServiceCollectionExtensions) đã tắt kiểm tra chứng chỉ.</summary>
    public const string HttpClientName = "IdentityServerApi";

    // Request body theo mẫu Bosch giữ tên thuộc tính PascalCase (ApiResource/UserName/RoleNames…), nên
    // serialize bằng options mặc định. Ngược lại response IS4 có thể camelCase ⇒ deserialize không phân biệt hoa/thường.
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _config;

    public IdentityServerService(
        IHttpClientFactory httpClientFactory,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _httpContextAccessor = httpContextAccessor;
        _config = config;
    }

    private IConfigurationSection Section => _config.GetSection("IdentityServer");
    private string Endpoint(string key) => Section.GetValue<string>(key) ?? string.Empty;

    /// <summary>Tên API resource (vd HCP_CBO_API) — role được gắn theo resource này khi assign/withdraw.</summary>
    public string ApiName => Section.GetValue<string>("APIName") ?? string.Empty;

    // Client dùng chung pool handler (đã bỏ kiểm tra chứng chỉ); BaseAddress + bearer token gắn theo TỪNG
    // request vì token là của phiên người dùng hiện tại, không thể là default header của client pooled.
    private async Task<HttpClient> CreateClientAsync()
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var baseUrl = Section.GetValue<string>("BaseURL");
        if (!string.IsNullOrWhiteSpace(baseUrl))
            client.BaseAddress = new Uri(baseUrl);

        var httpContext = _httpContextAccessor.HttpContext;
        var token = httpContext is null ? null : await httpContext.GetTokenAsync("access_token");
        if (!string.IsNullOrWhiteSpace(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    /// <summary>Danh sách role của API resource hiện tại (GET RolesByAPIName/{APIName}).</summary>
    public async Task<List<IdentityServerRoleResponse>> GetAllRolesAsync()
    {
        var client = await CreateClientAsync();
        var response = await client.GetAsync($"{Endpoint("RolesByAPIName")}/{ApiName}");
        if (!response.IsSuccessStatusCode)
            return new List<IdentityServerRoleResponse>();

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<IdentityServerRoleResponse>>(content, ReadOptions)
            ?? new List<IdentityServerRoleResponse>();
    }

    /// <summary>Gợi ý người dùng LDAP theo từ khóa (GET UserByName) — dùng cho ô autocomplete.</summary>
    public async Task<List<LdapUserResponse>> SearchLdapUserAsync(string searchKey)
    {
        var template = Endpoint("UserByName");
        // Template dạng "...UserByName?searchKey={0}&organizeKey=" — encode từ khóa để an toàn khi có dấu cách/ký tự đặc biệt.
        var apiUrl = string.Format(template, Uri.EscapeDataString(searchKey ?? string.Empty));

        var client = await CreateClientAsync();
        var response = await client.GetAsync(apiUrl);
        if (!response.IsSuccessStatusCode)
            return new List<LdapUserResponse>();

        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<LdapUserResponse>>(content, ReadOptions)
            ?? new List<LdapUserResponse>();
    }

    /// <summary>Người dùng đang được gán một tập role (POST IdentityServerUser) — dùng để hiển thị & thu hồi.</summary>
    public async Task<List<LdapUserResponse>> GetUsersByRoleAsync(UserByRoleRequest request)
    {
        var client = await CreateClientAsync();
        var content = JsonContent(request);
        var response = await client.PostAsync(Endpoint("IdentityServerUser"), content);
        if (!response.IsSuccessStatusCode)
            return new List<LdapUserResponse>();

        var result = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<LdapUserResponse>>(result, ReadOptions)
            ?? new List<LdapUserResponse>();
    }

    /// <summary>Gán role cho user (POST RoleForUser). Trả về true nếu IS4 phản hồi thành công.</summary>
    public async Task<bool> AssignRoleAsync(AssignRoleRequest request)
    {
        var client = await CreateClientAsync();
        var response = await client.PostAsync(Endpoint("RoleForUser"), JsonContent(request));
        return response.IsSuccessStatusCode;
    }

    /// <summary>Thu hồi role của user (POST WithdrawalRole). Trả về true nếu IS4 phản hồi thành công.</summary>
    public async Task<bool> WithdrawRoleAsync(AssignRoleRequest request)
    {
        var client = await CreateClientAsync();
        var response = await client.PostAsync(Endpoint("WithdrawalRole"), JsonContent(request));
        return response.IsSuccessStatusCode;
    }

    // Serialize theo options mặc định để giữ tên thuộc tính PascalCase như API Bosch mong đợi.
    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
}
