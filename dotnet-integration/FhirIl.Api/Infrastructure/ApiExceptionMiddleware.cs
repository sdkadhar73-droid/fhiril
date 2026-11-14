using Microsoft.AspNetCore.Mvc;
using Hl7.Fhir.Rest;

namespace FhirIl.Api.Infrastructure;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var status = exception switch
            {
                FhirOperationException fhirError => MapFhirStatus(fhirError.Status),
                UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => StatusCodes.Status404NotFound,
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => StatusCodes.Status503ServiceUnavailable,
                HttpRequestException => StatusCodes.Status502BadGateway,
                TimeoutException => StatusCodes.Status504GatewayTimeout,
                OperationCanceledException => StatusCodes.Status504GatewayTimeout,
                ArgumentException => StatusCodes.Status400BadRequest,
                InvalidOperationException => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status500InternalServerError
            };
            logger.LogError("Request failed. CorrelationId={CorrelationId} StatusCode={StatusCode} ExceptionType={ExceptionType}", context.TraceIdentifier, status, exception.GetType().Name);
            if (context.Response.HasStarted) throw;
            context.Response.Clear();
            context.Response.StatusCode = status;
            var details = new ProblemDetails
            {
                Status = status,
                Title = status >= 500 ? "The request could not be completed." : "The request was rejected.",
                Detail = status >= 500 ? "Check the server logs using the correlation ID." : "The request was invalid or could not be authorized.",
                Instance = context.Request.Path,
                Extensions = { ["correlationId"] = context.TraceIdentifier }
            };
            if (exception is FhirOperationException { Outcome: not null } operationException)
                details.Extensions["fhirIssueCodes"] = operationException.Outcome.Issue.Select(issue => issue.Code?.ToString()).Where(code => !string.IsNullOrWhiteSpace(code)).Distinct().ToArray();
            await context.Response.WriteAsJsonAsync(details);
        }
    }

    private static int MapFhirStatus(System.Net.HttpStatusCode? status) => status switch
    {
        System.Net.HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => StatusCodes.Status502BadGateway,
        System.Net.HttpStatusCode.NotFound => StatusCodes.Status404NotFound,
        System.Net.HttpStatusCode.Conflict => StatusCodes.Status409Conflict,
        System.Net.HttpStatusCode.UnprocessableEntity => StatusCodes.Status422UnprocessableEntity,
        System.Net.HttpStatusCode.TooManyRequests => StatusCodes.Status503ServiceUnavailable,
        _ when status is not null && (int)status >= 500 => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status502BadGateway
    };
}
