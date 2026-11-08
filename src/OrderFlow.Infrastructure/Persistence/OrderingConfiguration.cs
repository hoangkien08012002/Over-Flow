using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Ordering;
using OrderFlow.Infrastructure.Identity;

namespace OrderFlow.Infrastructure.Persistence;

internal sealed class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> e)
    { e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.HasIndex(x => x.UserId).IsUnique();
      e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
      e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.CartId).OnDelete(DeleteBehavior.Cascade); }
}
internal sealed class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> e)
    { e.ToTable("CartItems", t => t.HasCheckConstraint("CK_CartItems_Quantity", "[Quantity] BETWEEN 1 AND 100"));
      e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.HasIndex(x => new { x.CartId, x.ProductId }).IsUnique();
      e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict); }
}
internal sealed class ShippingMethodConfiguration : IEntityTypeConfiguration<ShippingMethod>
{
    public void Configure(EntityTypeBuilder<ShippingMethod> e)
    { e.ToTable("ShippingMethods", t => t.HasCheckConstraint("CK_ShippingMethods_Fee", "[Fee] >= 0"));
      e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.Code).HasMaxLength(40).IsRequired();
      e.Property(x => x.Name).HasMaxLength(150).IsRequired(); e.Property(x => x.Fee).HasColumnType("decimal(18,2)");
      e.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength(); e.Property(x => x.RowVersion).IsRowVersion(); e.HasIndex(x => x.Code).IsUnique();
      e.HasData(new { Id = Guid.Parse("5b6e892f-486b-4471-9d84-4d33b13cbe01"), Code = "STANDARD", Name = "Standard delivery", Fee = 30000m, CurrencyCode = "VND", IsActive = true,
        CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), UpdatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }); }
}
internal sealed class CouponConfiguration : IEntityTypeConfiguration<Coupon>
{
    public void Configure(EntityTypeBuilder<Coupon> e)
    { e.ToTable("Coupons", t => { t.HasCheckConstraint("CK_Coupons_Values", "[DiscountValue] >= 0 AND [MinimumSubtotal] >= 0 AND [UsageCount] >= 0");
      t.HasCheckConstraint("CK_Coupons_Dates", "[ValidUntilUtc] > [ValidFromUtc]"); });
      e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.Code).HasMaxLength(100).IsRequired(); e.HasIndex(x => x.Code).IsUnique();
      e.Property(x => x.DiscountType).HasConversion<string>().HasMaxLength(20); e.Property(x => x.DiscountValue).HasColumnType("decimal(18,2)");
      e.Property(x => x.MinimumSubtotal).HasColumnType("decimal(18,2)"); e.Property(x => x.RowVersion).IsRowVersion(); }
}
internal sealed class CouponUsageConfiguration : IEntityTypeConfiguration<CouponUsage>
{
    public void Configure(EntityTypeBuilder<CouponUsage> e)
    { e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.DiscountAmount).HasColumnType("decimal(18,2)");
      e.HasIndex(x => new { x.OrderId, x.CouponId }).IsUnique(); e.HasIndex(x => new { x.CouponId, x.UserId });
      e.HasOne<Coupon>().WithMany().HasForeignKey(x => x.CouponId).OnDelete(DeleteBehavior.Restrict);
      e.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
      e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict); }
}
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> e)
    { e.ToTable("Orders", t => t.HasCheckConstraint("CK_Orders_Money", "[Subtotal] >= 0 AND [DiscountTotal] >= 0 AND [DiscountTotal] <= [Subtotal] AND [ShippingFee] >= 0 AND [GrandTotal] >= 0"));
      e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.OrderNumber).HasMaxLength(32).IsRequired(); e.HasIndex(x => x.OrderNumber).IsUnique();
      e.HasIndex(x => new { x.UserId, x.CreatedAtUtc }); e.HasIndex(x => new { x.Status, x.CreatedAtUtc }); e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
      foreach (var name in new[] { nameof(Order.Subtotal), nameof(Order.DiscountTotal), nameof(Order.ShippingFee), nameof(Order.GrandTotal) }) e.Property<decimal>(name).HasColumnType("decimal(18,2)");
      e.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength(); e.Property(x => x.PaymentMethod).HasMaxLength(30); e.Property(x => x.ShippingMethodName).HasMaxLength(150);
      e.Property(x => x.AddressSnapshotJson).HasMaxLength(CheckoutService.MaxAddressSnapshotLength); e.Property(x => x.RowVersion).IsRowVersion();
      e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
      e.HasOne<ShippingMethod>().WithMany().HasForeignKey(x => x.ShippingMethodId).OnDelete(DeleteBehavior.Restrict);
      e.HasOne<Coupon>().WithMany().HasForeignKey(x => x.CouponId).OnDelete(DeleteBehavior.Restrict);
      e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
      e.HasMany(x => x.History).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict); }
}
internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> e)
    { e.ToTable("OrderItems", t => t.HasCheckConstraint("CK_OrderItems_Values", "[Quantity] > 0 AND [UnitPrice] >= 0 AND [LineSubtotal] >= 0 AND [DiscountAmount] >= 0 AND [DiscountAmount] <= [LineSubtotal]"));
      e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.SkuSnapshot).HasMaxLength(64); e.Property(x => x.ProductNameSnapshot).HasMaxLength(200);
      foreach (var name in new[] { nameof(OrderItem.UnitPrice), nameof(OrderItem.LineSubtotal), nameof(OrderItem.DiscountAmount) }) e.Property<decimal>(name).HasColumnType("decimal(18,2)");
      e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict); }
}
internal sealed class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> e)
    { e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(30);
      e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(30); e.Property(x => x.Reason).HasMaxLength(500); e.Property(x => x.CorrelationId).HasMaxLength(100);
      e.HasIndex(x => new { x.OrderId, x.ChangedAtUtc }); }
}
internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> e)
    { e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.Endpoint).HasMaxLength(150); e.Property(x => x.Key).HasMaxLength(100)
      .UseCollation("Latin1_General_100_BIN2"); e.Property(x => x.RequestHash).HasMaxLength(64); e.Property(x => x.Status).HasMaxLength(20);
      e.HasIndex(x => new { x.UserId, x.Endpoint, x.Key }).IsUnique(); e.HasIndex(x => x.ExpiresAtUtc);
      e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict); }
}
