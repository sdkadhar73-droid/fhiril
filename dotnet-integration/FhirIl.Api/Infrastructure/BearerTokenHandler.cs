using System.Net.Http.Headers;
using FhirIl.Api.Authentication;

namespace FhirIl.Api.Infrastructure;

public sealed class BearerTokenHandler(IAccessTokenProvider tokenProvider, IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var correlationId = httpContextAccessor.HttpContext?.TraceIdentifier;
        if (!string.IsNullOrWhiteSpace(correlationId)) request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        return await base.SendAsync(request, cancellationToken);
    }
}
