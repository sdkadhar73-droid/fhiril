using System.Net;

namespace FhirIl.Api.Infrastructure;

public sealed class ReadRetryHandler(Func<TimeSpan, CancellationToken, Task>? delay = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            return await base.SendAsync(request, cancellationToken);

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try { response = await base.SendAsync(request, cancellationToken); }
            catch (HttpRequestException) when (attempt < 2)
            {
                await DelayAsync(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
                continue;
            }
            if (attempt >= 2 || !IsTransient(response.StatusCode)) return response;
            var retryAfter = response.Headers.RetryAfter;
            var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date
                ? date - DateTimeOffset.UtcNow
                : TimeSpan.FromMilliseconds(250 * (attempt + 1)));
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            response.Dispose();
            await DelayAsync(delay > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delay, cancellationToken);
        }
    }

    private Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        delay is null ? Task.Delay(duration, cancellationToken) : delay(duration, cancellationToken);

    private static bool IsTransient(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;
}
