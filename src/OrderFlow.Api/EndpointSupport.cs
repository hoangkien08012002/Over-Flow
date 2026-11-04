using System.Security.Claims;
using OrderFlow.Domain.Administration;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Api;

internal static class EndpointSupport
{
    public static Guid UserId(HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub")!);
    public static string Correlation(HttpContext context) => context.Items[CorrelationIdMiddleware.ItemKey]?.ToString() ?? "";

    public static IResult Error(HttpContext context, int status, string code, string? title = null) => Results.Json(new
    {
        type = $"https://orderflow/errors/{code.ToLowerInvariant()}", title = title ?? code.Replace('_', ' '), status, code, correlationId = Correlation(context)
    }, statusCode: status, contentType: "application/problem+json");

    public static bool TryPage(int? page, int? pageSize, out int currentPage, out int currentPageSize)
    {
        currentPage = page ?? 1;
        currentPageSize = pageSize ?? 20;
        return currentPage >= 1 && currentPageSize is >= 1 and <= 100;
    }

    public static object Paged<T>(IEnumerable<T> items, int page, int pageSize, int totalCount) =>
        new { items, page, pageSize, totalCount, totalPages = (totalCount + pageSize - 1) / pageSize };

    public static bool TryVersion(string? input, out byte[]? value)
    {
        value = null;
        if (input is null) return true;
        try { value = Convert.FromBase64String(input); return value.Length == 8; }
        catch (FormatException) { return false; }
    }

    public static IResult ToResult<T>(HttpContext context, ServiceResult<T> result, Func<T, object> map, string? location = null) where T : class
    {
        if (result.Code is not null) return Error(context, result.StatusCode, result.Code);
        var body = map(result.Value!);
        return result.StatusCode == 201 ? Results.Created(location, body) : Results.Ok(body);
    }
}

// The filter runs after the handler has committed, so a failed mutation leaves no entry. If writing the entry fails, the mutation stands and the error is logged:
// failing a request whose change already committed would only invite a repeat. Order, shipment, and return history rows are the atomic record of those transitions.
internal static class Audit
{
    public const string TargetItem = "OrderFlow.AuditTarget";
    public const string ReasonItem = "OrderFlow.AuditReason";

    public static RouteHandlerBuilder Audited(this RouteHandlerBuilder builder, string action, string targetType) => builder.AddEndpointFilter(async (invocation, next) =>
    {
        var result = await next(invocation);
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        if (status is >= 200 and < 300) await WriteAsync(invocation.HttpContext, action, targetType, status);
        return result;
    });

    public static void Target(this HttpContext context, object id) => context.Items[TargetItem] = id.ToString();
    public static void Reason(this HttpContext context, string? reason) => context.Items[ReasonItem] = reason;

    private static async Task WriteAsync(HttpContext context, string action, string targetType, int status)
    {
        var target = context.Items[TargetItem]?.ToString() ?? context.Request.RouteValues.Values.Select(x => x?.ToString()).FirstOrDefault(x => Guid.TryParse(x, out _));
        var actor = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var id) ? id : (Guid?)null;
        try
        {
            await using var scope = context.RequestServices.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderFlowDbContext>();
            db.AuditEntries.Add(AuditEntry.Create(actor, action, targetType, target, status, context.Items[ReasonItem]?.ToString(), EndpointSupport.Correlation(context), scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow()));
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OrderFlow.Audit")
                .LogError("Audit entry for {Action} on {Target} could not be written: {ErrorType}", action, target, exception.GetType().Name);
        }
    }
}
