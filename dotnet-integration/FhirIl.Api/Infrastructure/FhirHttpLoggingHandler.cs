using System.Diagnostics;

namespace FhirIl.Api.Infrastructure;

public sealed class FhirHttpLoggingHandler(string operation, ILogger logger, IHttpContextAccessor contextAccessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var actualOperation = request.RequestUri?.AbsolutePath.TrimEnd('/').EndsWith("/metadata", StringComparison.OrdinalIgnoreCase) == true
                ? "FHIR.metadata"
                : operation;
            logger.LogInformation("FHIR HTTP request completed. CorrelationId={CorrelationId} Operation={Operation} StatusCode={StatusCode} DurationMs={DurationMs}",
                contextAccessor.HttpContext?.TraceIdentifier, actualOperation, (int)response.StatusCode, watch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception exception)
        {
            logger.LogWarning("FHIR HTTP request failed before a response. CorrelationId={CorrelationId} Operation={Operation} ExceptionType={ExceptionType} DurationMs={DurationMs}",
                contextAccessor.HttpContext?.TraceIdentifier, operation, exception.GetType().Name, watch.ElapsedMilliseconds);
            throw;
        }
    }
}
