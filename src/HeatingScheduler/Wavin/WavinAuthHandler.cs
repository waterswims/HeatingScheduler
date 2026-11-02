using System.Net;
using System.Net.Http.Headers;

namespace HeatingScheduler.Wavin;

/// <summary>Adds the Wavin bearer token to requests and signs in again once if the API returns 401.</summary>
public sealed class WavinAuthHandler(WavinAuthenticator authenticator) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await authenticator.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        authenticator.Invalidate(token);
        token = await authenticator.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
