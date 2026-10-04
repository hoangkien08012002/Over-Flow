using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Inventory;
using OrderFlow.Infrastructure.Identity;

namespace OrderFlow.Infrastructure.Persistence;

public sealed class OrderFlowDbContext(DbContextOptions<OrderFlowDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<InventoryAdjustment> InventoryAdjustments => Set<InventoryAdjustment>();

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
