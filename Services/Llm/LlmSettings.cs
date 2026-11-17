namespace ICOGenerator.Services.Llm;

/// <summary>
/// Cấu hình chung của MỌI lời gọi LLM (section <c>"Llm"</c> trong appsettings.json), gom về một chỗ:
///
///   "Llm": { "RequestTimeoutSeconds": 600, "TestConnectionTimeoutSeconds": 30,
///            "Proxy": { "Enabled": true, "Address": "http://127.0.0.1:3128" } }
///
/// Trước đây mỗi nơi gọi model tự đọc <c>Llm:RequestTimeoutSeconds</c> kèm một hằng mặc định RIÊNG
/// (<c>LlmClient</c>, <c>AgentRunService</c>, <c>EvalRunnerService</c>) — sửa mặc định ở một chỗ là lệch
/// ngay với hai chỗ kia. Giờ chỉ còn một nguồn: cùng kiểu <see cref="Budget.BudgetPolicy"/>, POCO này có
/// ctor nhận <see cref="IConfiguration"/> cho DI và một ctor thuần cho test.
/// </summary>
public sealed class LlmSettings
{
    public const string SectionName = "Llm";

    /// <summary>Deadline TỔNG cho một lời gọi model (timeout mạng của SDK bị tắt trong <see cref="OpenAIChatClientFactory"/>).</summary>
    public int RequestTimeoutSeconds { get; }

    /// <summary>
    /// Deadline riêng &amp; ngắn cho nút "Test Connection" ở trang Models: người dùng đang đứng chờ trước
    /// modal nên không thể dùng chung deadline 600s của một lượt agent.
    /// </summary>
    public int TestConnectionTimeoutSeconds { get; }

    /// <summary>Bật proxy cho endpoint KHÔNG phải localhost (mặc định bật để giữ hành vi ở mạng văn phòng).</summary>
    public bool ProxyEnabled { get; }

    public string ProxyAddress { get; }

    /// <summary>
    /// Trả lời <c>407 Proxy Authentication Required</c> bằng tài khoản Windows đang chạy app
    /// (Negotiate/Kerberos hoặc NTLM). Cần khi trỏ THẲNG vào proxy công ty; để tắt thì app không gửi
    /// credential nào — đúng cho relay local kiểu px/CNTLM (chúng tự xác thực hộ) và cho mọi máy không
    /// phải Windows. Mặc định TẮT: bật ngầm là lặng lẽ đem danh tính người dùng gửi ra một máy chủ mà
    /// người vận hành chưa chủ động khai.
    /// </summary>
    public bool ProxyUseDefaultCredentials { get; }

    /// <summary>
    /// Các host KHÔNG đi qua proxy — mỗi mục là một host đầy đủ (<c>llm.corp.local</c>) hoặc hậu tố tên
    /// miền (<c>.bosch.com</c>, <c>*.bosch.com</c>), KHÔNG phải regex. Cần vì proxy công ty thường từ chối
    /// chính các đích nội bộ: không có danh sách này thì bật proxy đồng nghĩa với việc mọi model tự host
    /// trong mạng nội bộ chết theo. Tương đương biến môi trường <c>no_proxy</c>.
    /// </summary>
    public IReadOnlyList<string> ProxyBypassList { get; }

    /// <summary>
    /// Đường dẫn lệnh <c>claude</c> (Claude Code CLI) cho các model có Endpoint <c>claude-code://…</c>. Trống ⇒
    /// tự tìm trong PATH và các chỗ cài hay gặp, xem <see cref="ClaudeCodeCli.FindBinary"/>.
    /// </summary>
    public string ClaudeCodeBinaryPath { get; }

    /// <summary>
    /// Biến môi trường ĐẶT ĐÈ cho tiến trình <c>claude</c> (section <c>Llm:ClaudeCode:Environment</c>), vd.
    /// <c>HTTPS_PROXY</c> trỏ vào relay px/CNTLM. Cần vì CLI tự kết nối ra Anthropic bằng mạng của riêng nó:
    /// <c>Llm:Proxy</c> chỉ áp cho HttpClient của app, còn biến <c>HTTPS_PROXY</c> của máy thường trỏ thẳng
    /// vào proxy công ty đòi NTLM/Kerberos mà CLI không trả lời được. Giá trị rỗng = gỡ biến đó khỏi môi trường
    /// của CLI. Xem <see cref="ClaudeCodeCli.ApplyEnvironment"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> ClaudeCodeEnvironment { get; }

    public LlmSettings(IConfiguration configuration)
        : this(
            configuration.GetValue($"{SectionName}:RequestTimeoutSeconds", DefaultRequestTimeoutSeconds),
            configuration.GetValue($"{SectionName}:TestConnectionTimeoutSeconds", DefaultTestConnectionTimeoutSeconds),
            configuration.GetValue($"{SectionName}:Proxy:Enabled", true),
            configuration.GetValue($"{SectionName}:Proxy:Address", DefaultProxyAddress) ?? DefaultProxyAddress,
            configuration.GetValue($"{SectionName}:Proxy:UseDefaultCredentials", false),
            configuration.GetSection($"{SectionName}:Proxy:BypassList").Get<string[]>(),
            configuration.GetValue<string>($"{SectionName}:ClaudeCode:BinaryPath"),
            configuration.GetSection($"{SectionName}:ClaudeCode:Environment").GetChildren()
                .Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
    {
    }

    public LlmSettings(
        int requestTimeoutSeconds = DefaultRequestTimeoutSeconds,
        int testConnectionTimeoutSeconds = DefaultTestConnectionTimeoutSeconds,
        bool proxyEnabled = true,
        string proxyAddress = DefaultProxyAddress,
        bool proxyUseDefaultCredentials = false,
        IEnumerable<string>? proxyBypassList = null,
        string? claudeCodeBinaryPath = null,
        IEnumerable<KeyValuePair<string, string?>>? claudeCodeEnvironment = null)
    {
        ClaudeCodeBinaryPath = claudeCodeBinaryPath?.Trim() ?? string.Empty;
        // Mục khai sau thắng mục khai trước khi trùng tên (không phân biệt hoa thường — trùng như vậy chỉ là gõ lặp).
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in claudeCodeEnvironment ?? Enumerable.Empty<KeyValuePair<string, string?>>())
            if (!string.IsNullOrWhiteSpace(name))
                environment[name.Trim()] = value?.Trim() ?? string.Empty;
        ClaudeCodeEnvironment = environment;
        // Deadline <= 0 sẽ khiến mọi lời gọi hủy ngay lập tức — coi cấu hình xấu như "dùng mặc định".
        RequestTimeoutSeconds = requestTimeoutSeconds > 0 ? requestTimeoutSeconds : DefaultRequestTimeoutSeconds;
        TestConnectionTimeoutSeconds = testConnectionTimeoutSeconds > 0 ? testConnectionTimeoutSeconds : DefaultTestConnectionTimeoutSeconds;
        ProxyEnabled = proxyEnabled;
        ProxyAddress = proxyAddress;
        ProxyUseDefaultCredentials = proxyUseDefaultCredentials;
        ProxyBypassList = (proxyBypassList ?? Enumerable.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToArray();
    }

    private const int DefaultRequestTimeoutSeconds = 600;
    private const int DefaultTestConnectionTimeoutSeconds = 30;
    private const string DefaultProxyAddress = "http://127.0.0.1:3128";
}
