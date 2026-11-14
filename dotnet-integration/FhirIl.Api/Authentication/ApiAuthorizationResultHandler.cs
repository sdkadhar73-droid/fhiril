using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace FhirIl.Api.Authentication;

public sealed class ApiAuthorizationResultHandler(ILogger<ApiAuthorizationResultHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden || authorizeResult.Challenged)
        {
            var status = authorizeResult.Forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
            logger.LogWarning("API authorization denied. CorrelationId={CorrelationId} StatusCode={StatusCode}", context.TraceIdentifier, status);
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = status,
                Title = authorizeResult.Forbidden ? "Insufficient authorization." : "Authentication is required.",
                Instance = context.Request.Path,
                Extensions = { ["correlationId"] = context.TraceIdentifier }
            });
            return;
        }
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
