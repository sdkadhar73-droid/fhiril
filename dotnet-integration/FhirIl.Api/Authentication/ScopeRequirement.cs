using FhirIl.Api.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace FhirIl.Api.Authentication;

public sealed class ScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}

public sealed class ScopeHandler(IOptions<OAuthOptions> options, IOptions<LocalOAuthOptions>? localOptions = null) : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        var mode = options.Value.Mode;
        if (mode.Equals("Anonymous", StringComparison.OrdinalIgnoreCase) && localOptions?.Value.Enabled != true)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
        if (options.Value.ApiAllowedScopes.Length > 0 && !options.Value.ApiAllowedScopes.Contains(requirement.Scope, StringComparer.Ordinal)) return Task.CompletedTask;
        var scopes = context.User.FindAll("scope").SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(context.User.FindAll("scp").SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        if (scopes.Contains(requirement.Scope, StringComparer.Ordinal)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
