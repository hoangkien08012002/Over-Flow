using Microsoft.EntityFrameworkCore;
using OrderFlow.Domain.Returns;
using OrderFlow.Domain.Shipping;
using OrderFlow.Infrastructure.Persistence;
using static OrderFlow.Api.EndpointSupport;

namespace OrderFlow.Api;

public static class FulfillmentEndpoints
{
    public static void MapFulfillmentEndpoints(this WebApplication app)
    {
        var shipments = app.MapGroup("/api/v1/shipments").RequireAuthorization("Fulfillment");
        shipments.MapPost("", CreateShipment).Audited("shipment.create", "Shipment");
        shipments.MapGet("", ListShipments);
        shipments.MapPost("/{id:guid}/status", ChangeShipmentStatus).Audited("shipment.status", "Shipment");
        app.MapGet("/api/v1/orders/{id:guid}/shipment", GetOrderShipment).RequireAuthorization();

        var customerReturns = app.MapGroup("/api/v1/orders/{id:guid}/returns").RequireAuthorization("Customer");
        customerReturns.MapPost("", RequestReturn);
        customerReturns.MapGet("", ListOrderReturns);

        var staffReturns = app.MapGroup("/api/v1/admin/returns");
        staffReturns.MapGet("", ListReturns).RequireAuthorization("ReturnStaff");
        staffReturns.MapGet("/{id:guid}", GetReturn).RequireAuthorization("ReturnStaff");
        staffReturns.MapPost("/{id:guid}/approve", ApproveReturn).RequireAuthorization("OrderStaff").Audited("return.approve", "ReturnRequest");
        staffReturns.MapPost("/{id:guid}/reject", RejectReturn).RequireAuthorization("OrderStaff").Audited("return.reject", "ReturnRequest");
        staffReturns.MapPost("/{id:guid}/receive", ReceiveReturn).RequireAuthorization("Fulfillment").Audited("return.receive", "ReturnRequest");
        staffReturns.MapPost("/{id:guid}/close", CloseReturn).RequireAuthorization("OrderStaff").Audited("return.close", "ReturnRequest");
    }

    private static async Task<IResult> CreateShipment(CreateShipmentRequest request, ShippingService service, HttpContext context, CancellationToken ct)
    {
        if (request.OrderId == Guid.Empty) return Error(context, 400, "VALIDATION_FAILED");
        var result = await service.CreateAsync(request.OrderId, UserId(context), Correlation(context), ct);
        if (result.Value is not null) context.Target(result.Value.Id);
        return ToResult(context, result, ToResponse, $"/api/v1/orders/{request.OrderId}/shipment");
    }

    private static async Task<IResult> ListShipments(OrderFlowDbContext db, HttpContext context, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        if (!TryPage(page, pageSize, out var currentPage, out var currentPageSize) || status is not null && !Enum.TryParse<ShipmentStatus>(status, true, out _))
            return Error(context, 400, "VALIDATION_FAILED");
        var query = db.Shipments.AsNoTracking();
        if (status is not null) { var parsed = Enum.Parse<ShipmentStatus>(status, true); query = query.Where(x => x.Status == parsed); }
        var count = await query.CountAsync(ct);
        var rows = await query.Include(x => x.History).OrderByDescending(x => x.CreatedAtUtc).Skip((currentPage - 1) * currentPageSize).Take(currentPageSize).AsSplitQuery().ToListAsync(ct);
        return Results.Ok(Paged(rows.Select(ToResponse), currentPage, currentPageSize, count));
    }

    private static async Task<IResult> ChangeShipmentStatus(Guid id, ShipmentStatusRequest request, ShippingService service, HttpContext context, CancellationToken ct)
    {
        context.Reason(request.Reason);
        return ToResult(context, await service.ChangeStatusAsync(id, request.Status, request.Reason, UserId(context), Correlation(context), ct), ToResponse);
    }

    // A customer sees only their own order's shipment, and any other answers exactly like a missing order.
    private static async Task<IResult> GetOrderShipment(Guid id, OrderFlowDbContext db, HttpContext context, CancellationToken ct)
    {
        var staff = context.User.IsInRole("Admin") || context.User.IsInRole("Support") || context.User.IsInRole("InventoryManager");
        var userId = UserId(context);
        if (!await db.Orders.AsNoTracking().AnyAsync(x => x.Id == id && (staff || x.UserId == userId), ct)) return Error(context, 404, "ORDER_NOT_FOUND");
        var shipment = await db.Shipments.AsNoTracking().Include(x => x.History).SingleOrDefaultAsync(x => x.OrderId == id, ct);
        return shipment is null ? Error(context, 404, "RESOURCE_NOT_FOUND") : Results.Ok(ToResponse(shipment));
    }

    private static async Task<IResult> RequestReturn(Guid id, ReturnCreateRequest request, ReturnService service, HttpContext context, CancellationToken ct)
    {
        var result = await service.RequestAsync(UserId(context), id, request.Reason, Correlation(context), ct);
        return ToResult(context, result, ToResponse, result.Value is null ? null : $"/api/v1/orders/{id}/returns");
    }

    private static async Task<IResult> ListOrderReturns(Guid id, OrderFlowDbContext db, HttpContext context, CancellationToken ct)
    {
        var userId = UserId(context);
        if (!await db.Orders.AsNoTracking().AnyAsync(x => x.Id == id && x.UserId == userId, ct)) return Error(context, 404, "ORDER_NOT_FOUND");
        var rows = await db.ReturnRequests.AsNoTracking().Include(x => x.Items).Include(x => x.History).Where(x => x.OrderId == id).OrderByDescending(x => x.CreatedAtUtc).AsSplitQuery().ToListAsync(ct);
        return Results.Ok(rows.Select(ToResponse));
    }

    private static async Task<IResult> ListReturns(OrderFlowDbContext db, HttpContext context, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        if (!TryPage(page, pageSize, out var currentPage, out var currentPageSize) || status is not null && !Enum.TryParse<ReturnStatus>(status, true, out _))
            return Error(context, 400, "VALIDATION_FAILED");
        var query = db.ReturnRequests.AsNoTracking();
        if (status is not null) { var parsed = Enum.Parse<ReturnStatus>(status, true); query = query.Where(x => x.Status == parsed); }
        var count = await query.CountAsync(ct);
        var rows = await query.Include(x => x.Items).Include(x => x.History).OrderByDescending(x => x.CreatedAtUtc).Skip((currentPage - 1) * currentPageSize).Take(currentPageSize).AsSplitQuery().ToListAsync(ct);
        return Results.Ok(Paged(rows.Select(ToResponse), currentPage, currentPageSize, count));
    }

    private static async Task<IResult> GetReturn(Guid id, OrderFlowDbContext db, HttpContext context, CancellationToken ct)
    {
        var row = await db.ReturnRequests.AsNoTracking().Include(x => x.Items).Include(x => x.History).AsSplitQuery().SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? Error(context, 404, "RESOURCE_NOT_FOUND") : Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> ApproveReturn(Guid id, ReturnService service, HttpContext context, CancellationToken ct) =>
        ToResult(context, await service.ApproveAsync(id, UserId(context), Correlation(context), ct), ToResponse);

    private static async Task<IResult> RejectReturn(Guid id, ReturnDecisionRequest request, ReturnService service, HttpContext context, CancellationToken ct)
    {
        context.Reason(request.Reason);
        return ToResult(context, await service.RejectAsync(id, UserId(context), request.Reason, Correlation(context), ct), ToResponse);
    }

    private static async Task<IResult> ReceiveReturn(Guid id, ReturnReceiveRequest request, ReturnService service, HttpContext context, CancellationToken ct) =>
        ToResult(context, await service.ReceiveAsync(id, request.Items, UserId(context), Correlation(context), ct), ToResponse);

    private static async Task<IResult> CloseReturn(Guid id, ReturnService service, HttpContext context, CancellationToken ct) =>
        ToResult(context, await service.CloseAsync(id, UserId(context), Correlation(context), ct), ToResponse);

    private static object ToResponse(Shipment x) => new
    {
        x.Id, x.OrderId, x.Status, x.TrackingCode, x.CreatedAtUtc, x.UpdatedAtUtc,
        history = x.History.OrderBy(h => h.ChangedAtUtc).Select(h => new { h.FromStatus, h.ToStatus, h.ChangedAtUtc, h.ActorUserId, h.Reason, h.CorrelationId })
    };

    private static object ToResponse(ReturnRequest x) => new
    {
        x.Id, x.OrderId, x.UserId, x.Status, x.Reason, x.CreatedAtUtc, x.UpdatedAtUtc,
        items = x.Items.Select(i => new { i.Id, i.OrderItemId, i.ProductId, i.Quantity, i.Restocked }),
        history = x.History.OrderBy(h => h.ChangedAtUtc).Select(h => new { h.FromStatus, h.ToStatus, h.ChangedAtUtc, h.ActorUserId, h.EventId, h.Reason, h.CorrelationId })
    };
}

public sealed record CreateShipmentRequest(Guid OrderId);
public sealed record ShipmentStatusRequest(ShipmentStatus Status, string? Reason);
public sealed record ReturnCreateRequest(string? Reason);
public sealed record ReturnDecisionRequest(string? Reason);
public sealed record ReturnReceiveRequest(IReadOnlyList<ReceivedLine>? Items);
