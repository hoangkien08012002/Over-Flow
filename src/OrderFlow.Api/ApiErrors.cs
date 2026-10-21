using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OrderFlow.Infrastructure.Identity;

namespace OrderFlow.Api;

public static class ApiErrors
{
    public static async Task WriteAsync(HttpContext context, int status, string code, string title, IDictionary<string, string[]>? errors = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Type = $"https://orderflow/errors/{code.ToLowerInvariant()}", Title = title, Status = status,
            Extensions = { ["code"] = code, ["correlationId"] = context.Items[CorrelationIdMiddleware.ItemKey]?.ToString() ?? "" }
        };
        if (errors is not null) problem.Extensions["errors"] = errors;
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string ItemKey = "OrderFlow.CorrelationId";
    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
        var id = !string.IsNullOrWhiteSpace(supplied) && supplied.Length <= 100 && supplied.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? supplied : Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = id;
        context.Response.OnStarting(() => { context.Response.Headers["X-Correlation-Id"] = id; return Task.CompletedTask; });
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id })) await next(context);
    }
}

public sealed class ProblemDetailsExceptionMiddleware(RequestDelegate next, ILogger<ProblemDetailsExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (AuthenticationException exception)
        {
            var title = exception.Code switch { "EMAIL_ALREADY_EXISTS" => "Email already exists", "ACCOUNT_LOCKED" => "Account is locked", "AUTH_INVALID_CREDENTIALS" => "Invalid email or password", "REFRESH_TOKEN_REVOKED" => "Refresh token is invalid or revoked", _ => "Request validation failed" };
            await ApiErrors.WriteAsync(context, exception.StatusCode, exception.Code, title, exception.Errors);
        }
        catch (BadHttpRequestException)
        {
            await ApiErrors.WriteAsync(context, 400, "VALIDATION_FAILED", "Request validation failed", new Dictionary<string, string[]> { ["request"] = ["The request body is invalid."] });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled request failure");
            if (!context.Response.HasStarted) await ApiErrors.WriteAsync(context, 500, "INTERNAL_ERROR", "An unexpected error occurred");
        }
    }
}

public sealed class ActiveAccountMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, OrderFlow.Infrastructure.Persistence.OrderFlowDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (!Guid.TryParse(context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst("sub")?.Value, out var userId))
            {
                await ApiErrors.WriteAsync(context, 401, "AUTHENTICATION_REQUIRED", "Authentication is required");
                return;
            }
            var active = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Users, x => x.Id == userId && x.IsActive, context.RequestAborted);
            if (!active) { await ApiErrors.WriteAsync(context, 401, "AUTH_INVALID_CREDENTIALS", "Authentication is required"); return; }
        }
        await next(context);
    }
}

public sealed class UnhandledStatusProblemMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);
        if (context.Response.HasStarted || context.Response.ContentLength is not null) return;
        var (code, title) = context.Response.StatusCode switch
        {
            400 => ("VALIDATION_FAILED", "Request validation failed"),
            401 => ("AUTHENTICATION_REQUIRED", "Authentication is required"),
            403 => ("FORBIDDEN", "Access is forbidden"),
            404 => ("RESOURCE_NOT_FOUND", "Resource not found"),
            429 => ("RATE_LIMITED", "Too many requests"),
            _ => (null, null)
        };
        if (code is not null) await ApiErrors.WriteAsync(context, context.Response.StatusCode, code, title!);
    }
}
