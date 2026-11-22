using System.Net;
using System.Text;
using ICOGenerator.Configuration;
using ICOGenerator.Services.Organization;
using Xunit;

namespace ICOGenerator.Tests.Organization;

// Token client_credentials cho employee-api: xin một lần rồi dùng lại tới gần hạn, gặp 401 thì xin lại và
// gửi lại đúng một lần, thiếu secret thì báo đúng key phải nạp.
public class EmployeeApiAuthTests
{
    private static EmployeeApiSettings Settings(string secret = "s3cret") => new()
    {
        ClientCredential = new EmployeeApiClientCredentialSettings
        {
            TokenUrl = "https://idp.test/connect/token",
            ClientId = "ico",
            ClientSecret = secret,
            Scope = "employee_api",
        },
    };

    [Fact]
    public async Task Token_IsCachedUntilNearExpiry()
    {
        var idp = new TokenEndpoint(expiresIn: 3600);
        var time = new ManualTime();
        var provider = new ClientCredentialTokenProvider(new SingleClientFactory(idp), Settings(), time);

        Assert.Equal("token-1", await provider.GetTokenAsync());
        Assert.Equal("token-1", await provider.GetTokenAsync());
        Assert.Equal(1, idp.Calls);
        Assert.Contains("grant_type=client_credentials", idp.LastBody);
        Assert.Contains("scope=employee_api", idp.LastBody);

        time.Advance(TimeSpan.FromMinutes(59.5)); // còn < 1 phút ⇒ phải xin token mới.
        Assert.Equal("token-2", await provider.GetTokenAsync());
    }

    [Fact]
    public async Task MissingSecret_NamesTheEnvironmentVariable()
    {
        var provider = new ClientCredentialTokenProvider(new SingleClientFactory(new TokenEndpoint(60)), Settings(secret: ""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync());
        Assert.Contains("EmployeeApi__ClientCredential__ClientSecret", ex.Message);
    }

    [Fact]
    public async Task Unauthorized_RefreshesTokenAndRetriesOnce()
    {
        var idp = new TokenEndpoint(expiresIn: 3600);
        var provider = new ClientCredentialTokenProvider(new SingleClientFactory(idp), Settings());
        var api = new RecordingApi(firstStatus: HttpStatusCode.Unauthorized);
        using var client = new HttpClient(new EmployeeApiTokenHandler(provider) { InnerHandler = api });

        using var response = await client.GetAsync("https://employee.test/api/Departments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Bearer token-1", "Bearer token-2"], api.AuthorizationHeaders);
    }

    private sealed class TokenEndpoint(int expiresIn) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var json = $$"""{"access_token":"token-{{Calls}}","expires_in":{{expiresIn}},"token_type":"Bearer"}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class RecordingApi(HttpStatusCode firstStatus) : HttpMessageHandler
    {
        public List<string> AuthorizationHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationHeaders.Add(request.Headers.Authorization!.ToString());
            var status = AuthorizationHeaders.Count == 1 ? firstStatus : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 22, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
