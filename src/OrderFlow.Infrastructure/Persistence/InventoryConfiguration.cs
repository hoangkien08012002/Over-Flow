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
