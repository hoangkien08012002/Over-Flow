using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Inventory;
using OrderFlow.Domain.Notifications;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Payments;
using OrderFlow.Infrastructure.Identity;

namespace OrderFlow.Infrastructure.Persistence;

public sealed class OrderFlowDbContext(DbContextOptions<OrderFlowDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<InventoryAdjustment> InventoryAdjustments => Set<InventoryAdjustment>();
    public DbSet<InventoryReservation> InventoryReservations => Set<InventoryReservation>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<ShippingMethod> ShippingMethods => Set<ShippingMethod>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<CouponUsage> CouponUsages => Set<CouponUsage>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderStatusHistory> OrderStatusHistories => Set<OrderStatusHistory>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<FailedMessage> FailedMessages => Set<FailedMessage>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(OrderFlowDbContext).Assembly);
        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
            user.Property(x => x.LastName).HasMaxLength(100).IsRequired();
            user.Property(x => x.IsActive).HasDefaultValue(true);
            user.Property(x => x.CreatedAtUtc).IsRequired();
        });
        builder.Entity<ApplicationUser>().HasIndex(x => x.NormalizedEmail).IsUnique().HasDatabaseName("EmailIndex").HasFilter("[NormalizedEmail] IS NOT NULL");

        SeedRoles(builder);
    }

    private static void SeedRoles(ModelBuilder builder)
    {
        var roles = new[] { "Customer", "Support", "InventoryManager", "Admin" };
        var ids = new[]
        {
            Guid.Parse("3f775888-8047-4f63-a553-f7934895f001"),
            Guid.Parse("3f775888-8047-4f63-a553-f7934895f002"),
            Guid.Parse("3f775888-8047-4f63-a553-f7934895f003"),
            Guid.Parse("3f775888-8047-4f63-a553-f7934895f004")
        };
        builder.Entity<IdentityRole<Guid>>().HasData(roles.Select((name, i) => new IdentityRole<Guid>
        {
            Id = ids[i], Name = name, NormalizedName = name.ToUpperInvariant(), ConcurrencyStamp = $"role-{ids[i]:N}"
        }));
    }
}
