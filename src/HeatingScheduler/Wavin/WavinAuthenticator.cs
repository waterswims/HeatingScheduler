using System.Globalization;
using System.Net.Http.Json;
using HeatingScheduler.Options;
using Microsoft.Extensions.Options;

namespace HeatingScheduler.Wavin;

/// <summary>Signs in to the Wavin Sentio cloud (Firebase identity toolkit) and caches the ID token until shortly before it expires.</summary>
public sealed class WavinAuthenticator(
    IHttpClientFactory httpClientFactory,
    IOptions<WavinOptions> options,
    TimeProvider timeProvider,
    ILogger<WavinAuthenticator> logger)
{
    public const string HttpClientName = "WavinAuth";
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is { } cached && timeProvider.GetUtcNow() < _expiresAt)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } t && timeProvider.GetUtcNow() < _expiresAt)
            {
                return t;
            }

            return await SignInAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forces the next <see cref="GetTokenAsync"/> to sign in again (e.g. after a 401).</summary>
    public void Invalidate(string rejectedToken)
    {
        if (_token == rejectedToken)
        {
            _token = null;
        }
    }

    private async Task<string> SignInAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = new Uri(o.AuthBaseUrl, $"v1/accounts:signInWithPassword?key={Uri.EscapeDataString(o.FirebaseApiKey)}");

        using var response = await client.PostAsJsonAsync(
            url, new FirebaseSignInRequest { Email = o.Email, Password = o.Password }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Wavin sign-in failed with {(int)response.StatusCode}: {body}", null, response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<FirebaseSignInResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Wavin sign-in returned an empty response.");

        var lifetime = int.TryParse(result.ExpiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromHours(1);

        _token = result.IdToken;
        _expiresAt = timeProvider.GetUtcNow() + lifetime - ExpiryMargin;
        logger.LogInformation("Signed in to Wavin Sentio as {Email}; token valid until {ExpiresAt:u}", o.Email, _expiresAt);
        return _token;
    }
}
