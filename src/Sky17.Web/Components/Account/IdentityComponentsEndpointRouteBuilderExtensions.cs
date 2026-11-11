using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Sky17.Infrastructure.Data;

namespace Microsoft.AspNetCore.Routing;

internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var accountGroup = endpoints.MapGroup("/Account");

        // POST + antiforgery (validated automatically for form posts in minimal APIs with [FromForm]).
        accountGroup.MapPost("/Logout", async (
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            var target = string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//") ? "/" : returnUrl;
            return TypedResults.LocalRedirect("~" + target);
        });

        return accountGroup;
    }
}
