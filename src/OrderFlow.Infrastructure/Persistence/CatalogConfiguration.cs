using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFlow.Domain.Catalog;

namespace OrderFlow.Infrastructure.Persistence;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> entity)
    {
        entity.ToTable("Categories", table => table.HasCheckConstraint("CK_Categories_Name", "LEN(TRIM([Name])) > 0"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.Name).HasMaxLength(150).IsRequired();
        entity.Property(x => x.Slug).HasMaxLength(180).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(1000);
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.HasIndex(x => x.Slug).IsUnique();
        entity.HasIndex(x => x.IsActive);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> entity)
    {
        entity.ToTable("Products", table =>
        {
            table.HasCheckConstraint("CK_Products_Price", "[Price] >= 0");
            table.HasCheckConstraint("CK_Products_CurrencyCode", "LEN([CurrencyCode]) = 3");
            table.HasCheckConstraint("CK_Products_Name", "LEN(TRIM([Name])) > 0");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.Sku).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(4000);
        entity.Property(x => x.Price).HasPrecision(18, 2);
        entity.Property(x => x.CurrencyCode).HasColumnType("char(3)").IsRequired();
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.HasIndex(x => x.Sku).IsUnique();
        entity.HasIndex(x => new { x.CategoryId, x.IsActive });
        entity.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}
