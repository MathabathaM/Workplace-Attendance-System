using Microsoft.AspNetCore.Mvc;
using Workplace.Api.Services;

namespace Workplace.Api.Middleware;

public sealed class TemporaryPasswordMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api") &&
            context.User.HasClaim(AppClaimsPrincipalFactory.MustChangePasswordClaim, "true") &&
            !IsAllowedDuringPasswordChange(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Change your temporary password before using the application."
            }, context.RequestAborted);
            return;
        }

        await next(context);
    }

    private static bool IsAllowedDuringPasswordChange(HttpRequest request) =>
        request.Path == "/api/auth/me" ||
        request.Path == "/api/auth/login" && HttpMethods.IsPost(request.Method) ||
        request.Path == "/api/auth/logout" && HttpMethods.IsPost(request.Method) ||
        request.Path == "/api/auth/change-password" && HttpMethods.IsPost(request.Method);
}
