namespace OrderFlow.Domain.Catalog;

public sealed class Category
{
    private Category() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string Slug { get; private set; } = null!;
    public string? Description { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Category Create(Guid id, string name, string slug, string? description, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        Validate(name, slug, description, now);
        return new Category { Id = id, Name = name.Trim(), Slug = slug.Trim(), Description = description?.Trim(), IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
    }

    public void Update(string name, string slug, string? description, DateTimeOffset now)
    {
        Validate(name, slug, description, now);
        Name = name.Trim();
        Slug = slug.Trim();
        Description = description?.Trim();
        UpdatedAtUtc = now;
    }

    public void SetActive(bool active, DateTimeOffset now)
    {
        RequireUtc(now);
        IsActive = active;
        UpdatedAtUtc = now;
    }

    private static void Validate(string name, string slug, string? description, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 150) throw new ArgumentException("Category name must be 1 to 150 characters.", nameof(name));
        if (string.IsNullOrWhiteSpace(slug) || slug.Trim().Length > 180) throw new ArgumentException("Category slug must be 1 to 180 characters.", nameof(slug));
        if (description?.Trim().Length > 1000) throw new ArgumentException("Category description is too long.", nameof(description));
        RequireUtc(now);
    }

    private static void RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamp must be UTC.", nameof(value));
    }
}
