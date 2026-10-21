using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace OrderFlow.Api;

public sealed class OwnResourceRequirement : IAuthorizationRequirement;

public sealed class OwnResourceHandler : AuthorizationHandler<OwnResourceRequirement, Guid>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OwnResourceRequirement requirement, Guid resourceOwnerId)
    {
        if (Guid.TryParse(context.User.FindFirst("sub")?.Value, out var currentUserId) && currentUserId == resourceOwnerId)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public static class OwnResourceAuthorization
{
    public static async Task<bool> RequireOwnerOrHideAsync(
        this HttpContext context,
        IAuthorizationService authorization,
        Guid resourceOwnerId,
        string notFoundCode = "RESOURCE_NOT_FOUND")
    {
        var result = await authorization.AuthorizeAsync(context.User, resourceOwnerId, "OwnResource");
        if (result.Succeeded) return true;
        await ApiErrors.WriteAsync(context, StatusCodes.Status404NotFound, notFoundCode, "Resource not found");
        return false;
    }
}
