using System.Net;
using System.Net.Http.Headers;

namespace ICOGenerator.Services.Organization;

/// <summary>
/// Gắn bearer token vào mọi request tới employee-api. Gặp 401 thì bỏ token đang giữ, xin token mới và gửi
/// lại ĐÚNG MỘT lần — token bị thu hồi trước hạn không được làm hỏng cả lần đồng bộ.
/// </summary>
public sealed class EmployeeApiTokenHandler : DelegatingHandler
{
    private readonly ClientCredentialTokenProvider _tokens;

    public EmployeeApiTokenHandler(ClientCredentialTokenProvider tokens)
    {
        _tokens = tokens;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(cancellationToken));
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        _tokens.Invalidate();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }
}
