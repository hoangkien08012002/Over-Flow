using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFlow.Domain.Administration;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Engagement;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Returns;
using OrderFlow.Domain.Shipping;
using OrderFlow.Infrastructure.Identity;

namespace OrderFlow.Infrastructure.Persistence;

internal sealed class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> entity)
    {
        entity.ToTable("Shipments", table => table.HasCheckConstraint("CK_Shipments_Status", "[Status] IN ('Pending','ReadyToShip','Shipped','Delivered','DeliveryFailed','Cancelled')"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.TrackingCode).HasMaxLength(40).IsRequired();
        entity.Property(x => x.RowVersion).IsRowVersion();
        // One shipment per order in the first release; this also settles two concurrent create requests.
        entity.HasIndex(x => x.OrderId).IsUnique();
        entity.HasIndex(x => x.TrackingCode).IsUnique();
        entity.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(x => x.History).WithOne().HasForeignKey(x => x.ShipmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ShipmentStatusHistoryConfiguration : IEntityTypeConfiguration<ShipmentStatusHistory>
{
    public void Configure(EntityTypeBuilder<ShipmentStatusHistory> entity)
    {
        entity.ToTable("ShipmentStatusHistories");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.Reason).HasMaxLength(500);
        entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        entity.HasIndex(x => new { x.ShipmentId, x.ChangedAtUtc });
        entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReturnRequestConfiguration : IEntityTypeConfiguration<ReturnRequest>
{
    public void Configure(EntityTypeBuilder<ReturnRequest> entity)
    {
        entity.ToTable("ReturnRequests", table => table.HasCheckConstraint("CK_ReturnRequests_Status", "[Status] IN ('Requested','Approved','Rejected','Received','Refunded','Closed')"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        entity.Property(x => x.RowVersion).IsRowVersion();
        // A rejected request may be followed by a new one inside the window; anything else blocks a second return of the order.
        entity.HasIndex(x => x.OrderId).IsUnique().HasFilter("[Status] <> 'Rejected'").HasDatabaseName("UX_ReturnRequests_OpenOrder");
        entity.HasIndex(x => new { x.Status, x.CreatedAtUtc });
        entity.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.ReturnRequestId).OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(x => x.History).WithOne().HasForeignKey(x => x.ReturnRequestId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReturnItemConfiguration : IEntityTypeConfiguration<ReturnItem>
{
    public void Configure(EntityTypeBuilder<ReturnItem> entity)
    {
        entity.ToTable("ReturnItems", table => table.HasCheckConstraint("CK_ReturnItems_Quantity", "[Quantity] > 0"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.HasIndex(x => new { x.ReturnRequestId, x.OrderItemId }).IsUnique();
        entity.HasOne<OrderItem>().WithMany().HasForeignKey(x => x.OrderItemId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReturnStatusHistoryConfiguration : IEntityTypeConfiguration<ReturnStatusHistory>
{
    public void Configure(EntityTypeBuilder<ReturnStatusHistory> entity)
    {
        entity.ToTable("ReturnStatusHistories");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.Reason).HasMaxLength(500);
        entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        entity.HasIndex(x => new { x.ReturnRequestId, x.ChangedAtUtc });
    }
}

internal sealed class WishlistItemConfiguration : IEntityTypeConfiguration<WishlistItem>
{
    public void Configure(EntityTypeBuilder<WishlistItem> entity)
    {
        entity.ToTable("WishlistItems");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.HasIndex(x => new { x.UserId, x.ProductId }).IsUnique();
        entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> entity)
    {
        entity.ToTable("Reviews", table =>
        {
            table.HasCheckConstraint("CK_Reviews_Rating", "[Rating] BETWEEN 1 AND 5");
            table.HasCheckConstraint("CK_Reviews_Status", "[Status] IN ('Published','Hidden')");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.Comment).HasMaxLength(Review.MaxCommentLength);
        entity.Property(x => x.ModerationReason).HasMaxLength(500);
        entity.Property(x => x.RowVersion).IsRowVersion();
        // One review per purchased line, which also settles two concurrent submissions.
        entity.HasIndex(x => x.OrderItemId).IsUnique();
        entity.HasIndex(x => new { x.ProductId, x.Status, x.CreatedAtUtc });
        entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<OrderItem>().WithMany().HasForeignKey(x => x.OrderItemId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> entity)
    {
        entity.ToTable("AuditEntries");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.Action).HasMaxLength(100).IsRequired();
        entity.Property(x => x.TargetType).HasMaxLength(50).IsRequired();
        entity.Property(x => x.TargetId).HasMaxLength(100);
        entity.Property(x => x.Reason).HasMaxLength(500);
        entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        entity.HasIndex(x => x.OccurredAtUtc);
        entity.HasIndex(x => new { x.TargetType, x.TargetId });
        entity.HasIndex(x => x.ActorUserId);
    }
}
