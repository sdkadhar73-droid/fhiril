using System.Net;
using System.Text.Json;
using FhirIl.Api.Infrastructure;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace FhirIl.UnitTests;

public sealed class ErrorHandlingTests
{
    [Fact]
    public async Task FhirOperationOutcomeIsMappedToProblemDetailsWithoutDiagnostics()
    {
        var outcome = new OperationOutcome();
        outcome.Issue.Add(new OperationOutcome.IssueComponent { Code = OperationOutcome.IssueType.Invalid, Diagnostics = "synthetic private detail must not be returned" });
        var middleware = new ApiExceptionMiddleware(_ => throw new FhirOperationException("sensitive error text", HttpStatusCode.UnprocessableEntity, outcome), NullLogger<ApiExceptionMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/fhir/patients";
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(422, json.RootElement.GetProperty("status").GetInt32());
        Assert.Contains(json.RootElement.GetProperty("fhirIssueCodes").EnumerateArray().Select(value => value.GetString()), value => value?.Equals("invalid", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain("synthetic private detail", json.RootElement.ToString());
        Assert.DoesNotContain("sensitive error text", json.RootElement.ToString());
    }

    [Fact]
    public async Task TimeoutReturnsProblemDetailsAndCorrelationId()
    {
        var middleware = new ApiExceptionMiddleware(_ => throw new TimeoutException(), NullLogger<ApiExceptionMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "correlation-test";
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("correlation-test", json.RootElement.GetProperty("correlationId").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized, StatusCodes.Status502BadGateway)]
    [InlineData(HttpStatusCode.Forbidden, StatusCodes.Status502BadGateway)]
    [InlineData(HttpStatusCode.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(HttpStatusCode.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(HttpStatusCode.TooManyRequests, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, StatusCodes.Status502BadGateway)]
    public async Task FhirHttpStatusMapsToSafeApiStatus(HttpStatusCode fhirStatus, int expectedStatus)
    {
        var middleware = new ApiExceptionMiddleware(_ => throw new FhirOperationException("sensitive upstream text", fhirStatus), NullLogger<ApiExceptionMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
    }
}
