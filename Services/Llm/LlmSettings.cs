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
    /// Deadline riêng & ngắn cho nút "Test Connection" ở trang Models: người dùng đang đứng chờ trước
    /// modal nên không thể dùng chung deadline 600s của một lượt agent.
    /// </summary>
    public int TestConnectionTimeoutSeconds { get; }

    /// <summary>Bật proxy cho endpoint KHÔNG phải localhost (mặc định bật để giữ hành vi ở mạng văn phòng).</summary>
    public bool ProxyEnabled { get; }

    public string ProxyAddress { get; }

    public LlmSettings(IConfiguration configuration)
        : this(
            configuration.GetValue($"{SectionName}:RequestTimeoutSeconds", DefaultRequestTimeoutSeconds),
            configuration.GetValue($"{SectionName}:TestConnectionTimeoutSeconds", DefaultTestConnectionTimeoutSeconds),
            configuration.GetValue($"{SectionName}:Proxy:Enabled", true),
            configuration.GetValue($"{SectionName}:Proxy:Address", DefaultProxyAddress) ?? DefaultProxyAddress)
    {
    }

    public LlmSettings(
        int requestTimeoutSeconds = DefaultRequestTimeoutSeconds,
        int testConnectionTimeoutSeconds = DefaultTestConnectionTimeoutSeconds,
        bool proxyEnabled = true,
        string proxyAddress = DefaultProxyAddress)
    {
        // Deadline <= 0 sẽ khiến mọi lời gọi hủy ngay lập tức — coi cấu hình xấu như "dùng mặc định".
        RequestTimeoutSeconds = requestTimeoutSeconds > 0 ? requestTimeoutSeconds : DefaultRequestTimeoutSeconds;
        TestConnectionTimeoutSeconds = testConnectionTimeoutSeconds > 0 ? testConnectionTimeoutSeconds : DefaultTestConnectionTimeoutSeconds;
        ProxyEnabled = proxyEnabled;
        ProxyAddress = proxyAddress;
    }

    private const int DefaultRequestTimeoutSeconds = 600;
    private const int DefaultTestConnectionTimeoutSeconds = 30;
    private const string DefaultProxyAddress = "http://127.0.0.1:3128";
}
