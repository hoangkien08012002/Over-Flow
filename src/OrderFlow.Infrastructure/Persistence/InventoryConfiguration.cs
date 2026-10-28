using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFlow.Domain.Inventory;
using OrderFlow.Domain.Catalog;
using OrderFlow.Infrastructure.Identity;

namespace OrderFlow.Infrastructure.Persistence;

internal sealed class InventoryConfiguration : IEntityTypeConfiguration<Inventory>
{
    public void Configure(EntityTypeBuilder<Inventory> entity)
    {
        entity.ToTable("Inventories", table =>
        {
            table.HasCheckConstraint("CK_Inventories_OnHand", "[OnHand] >= 0");
            table.HasCheckConstraint("CK_Inventories_Reserved", "[Reserved] >= 0 AND [Reserved] <= [OnHand]");
            table.HasCheckConstraint("CK_Inventories_Threshold", "[LowStockThreshold] >= 0");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.Ignore(x => x.Available);
        entity.HasIndex(x => x.ProductId).IsUnique();
        entity.HasOne<Product>().WithOne().HasForeignKey<Inventory>(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InventoryAdjustmentConfiguration : IEntityTypeConfiguration<InventoryAdjustment>
{
    public void Configure(EntityTypeBuilder<InventoryAdjustment> entity)
    {
        entity.ToTable("InventoryAdjustments", table => table.HasCheckConstraint("CK_InventoryAdjustments_QuantityDelta", "[QuantityDelta] <> 0"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.ReasonCode).HasMaxLength(50).IsRequired();
        entity.Property(x => x.Note).HasMaxLength(500);
        entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        entity.HasIndex(x => new { x.InventoryId, x.CreatedAtUtc });
        entity.HasOne<Inventory>().WithMany().HasForeignKey(x => x.InventoryId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InventoryReservationConfiguration : IEntityTypeConfiguration<InventoryReservation>
{
    public void Configure(EntityTypeBuilder<InventoryReservation> entity)
    {
        entity.ToTable("InventoryReservations", table =>
        {
            table.HasCheckConstraint("CK_InventoryReservations_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint("CK_InventoryReservations_Status", "[Status] IN ('Active','Committed','Released','Expired')");
            table.HasCheckConstraint("CK_InventoryReservations_Expiry", "[ExpiresAtUtc] > [CreatedAtUtc]");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.HasIndex(x => new { x.OrderId, x.ProductId }).IsUnique();
        entity.HasIndex(x => new { x.Status, x.ExpiresAtUtc });
        entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> entity)
    {
        entity.ToTable("OutboxMessages");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        entity.Property(x => x.Payload).IsRequired();
        entity.Property(x => x.LastError).HasMaxLength(1000);
        entity.HasIndex(x => new { x.ProcessedAtUtc, x.NextAttemptAtUtc, x.OccurredAtUtc });
    }
}
