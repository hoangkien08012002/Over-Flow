using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Application.Inventory;

namespace OrderFlow.Api;

public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/inventory").RequireAuthorization("InventoryManager");
        group.MapGet("/{productId:guid}", GetInventory);
        group.MapGet("/{productId:guid}/reservations", GetReservations);
        group.MapPost("/{productId:guid}/adjust", Adjust);
        group.MapPut("/{productId:guid}/threshold", SetThreshold);
    }

    private static async Task<IResult> GetInventory(Guid productId, IInventoryService service, HttpContext context, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(productId, cancellationToken);
        return result is null ? Error(context, 404, "RESOURCE_NOT_FOUND", "Inventory not found") : Results.Ok(result);
    }

    private static async Task<IResult> GetReservations(Guid productId, IInventoryService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.GetReservationsAsync(productId, cancellationToken));

    private static async Task<IResult> Adjust(Guid productId, AdjustInventoryRequest request, IInventoryService service, HttpContext context, CancellationToken cancellationToken)
    {
        if (request.QuantityDelta == 0 || string.IsNullOrWhiteSpace(request.ReasonCode) || request.ReasonCode.Trim().Length > 50 ||
            request.Note?.Trim().Length > 500 || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var actor))
            return Error(context, 400, "VALIDATION_FAILED", "Invalid inventory adjustment");
        try
        {
            var result = await service.AdjustAsync(productId, request.QuantityDelta, request.ReasonCode, request.Note, actor,
                context.Items[CorrelationIdMiddleware.ItemKey]?.ToString() ?? Guid.NewGuid().ToString("N"), cancellationToken);
            return result is null ? Error(context, 404, "RESOURCE_NOT_FOUND", "Inventory not found") : Results.Ok(result);
        }
        catch (InvalidOperationException) { return Error(context, 409, "INVENTORY_BELOW_ZERO", "Adjustment would make inventory invalid"); }
        catch (DbUpdateConcurrencyException) { return Error(context, 409, "CONCURRENCY_CONFLICT", "Inventory was changed"); }
    }

    private static async Task<IResult> SetThreshold(Guid productId, SetInventoryThresholdRequest request, IInventoryService service, HttpContext context, CancellationToken cancellationToken)
    {
        if (request.LowStockThreshold < 0) return Error(context, 400, "VALIDATION_FAILED", "Invalid low-stock threshold");
        var result = await service.SetLowStockThresholdAsync(productId, request.LowStockThreshold, cancellationToken);
        return result is null ? Error(context, 404, "RESOURCE_NOT_FOUND", "Inventory not found") : Results.Ok(result);
    }

    private static IResult Error(HttpContext context, int status, string code, string title) => Results.Json(new
    {
        type = $"https://orderflow/errors/{code.ToLowerInvariant()}", title, status, code,
        correlationId = context.Items[CorrelationIdMiddleware.ItemKey]?.ToString() ?? ""
    }, statusCode: status, contentType: "application/problem+json");
}

public sealed record AdjustInventoryRequest(int QuantityDelta, string? ReasonCode, string? Note);
public sealed record SetInventoryThresholdRequest(int LowStockThreshold);
