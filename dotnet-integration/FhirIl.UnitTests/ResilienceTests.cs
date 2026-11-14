using System.Net;
using FhirIl.Api.Infrastructure;

namespace FhirIl.UnitTests;

public sealed class ResilienceTests
{
    [Fact]
    public async Task GetRetriesTransientFailure()
    {
        var sends = 0;
        using var client = Client(_ => ++sends == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var response = await client.GetAsync("https://example.test/fhir/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task PostIsNotRetried()
    {
        var sends = 0;
        using var client = Client(_ => { sends++; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); });
        using var response = await client.PostAsync("https://example.test/fhir/Patient", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task GetHonorsRetryAfter()
    {
        var sends = 0;
        TimeSpan? requestedDelay = null;
        var handler = new ReadRetryHandler((duration, _) => { requestedDelay = duration; return Task.CompletedTask; })
        {
            InnerHandler = new StubHandler(_ =>
            {
                if (++sends == 1)
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                    return limited;
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            })
        };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("https://example.test/fhir/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), requestedDelay);
        Assert.Equal(2, sends);
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new ReadRetryHandler { InnerHandler = new StubHandler(respond) });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }
}
