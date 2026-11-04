using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFlow.Domain.Notifications;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Payments;

namespace OrderFlow.Infrastructure.Persistence;

public sealed class InboxMessage
{
    public string ConsumerName { get; set; } = null!;
    public Guid EventId { get; set; }
    public string EventType { get; set; } = null!;
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
}

public static class FailedMessageStatus
{
    public const string Failed = "Failed";
    public const string ReplayRequested = "ReplayRequested";
    public const string Replayed = "Replayed";
}

public sealed class FailedMessage
{
    public Guid Id { get; set; }
    public string ConsumerName { get; set; } = null!;
    public string SourceTopic { get; set; } = null!;
    public int Partition { get; set; }
    public long Offset { get; set; }
    public Guid? EventId { get; set; }
    public string? EventType { get; set; }
    public string? MessageKey { get; set; }
    public string RawMessage { get; set; } = null!;
    public int AttemptCount { get; set; }
    public string ErrorSummary { get; set; } = null!;
    public DateTimeOffset FailedAtUtc { get; set; }
    public DateTimeOffset? DeadLetteredAtUtc { get; set; }
    public string Status { get; set; } = FailedMessageStatus.Failed;
    public DateTimeOffset? ReplayRequestedAtUtc { get; set; }
    public DateTimeOffset? ReplayedAtUtc { get; set; }
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> entity)
    {
        entity.ToTable("InboxMessages");
        entity.HasKey(x => new { x.ConsumerName, x.EventId });
        entity.Property(x => x.ConsumerName).HasMaxLength(100);
        entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        entity.HasIndex(x => x.ProcessedAtUtc);
    }
}

internal sealed class FailedMessageConfiguration : IEntityTypeConfiguration<FailedMessage>
{
    public void Configure(EntityTypeBuilder<FailedMessage> entity)
    {
        entity.ToTable("FailedMessages");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.ConsumerName).HasMaxLength(100).IsRequired();
        entity.Property(x => x.SourceTopic).HasMaxLength(255).IsRequired();
        entity.Property(x => x.EventType).HasMaxLength(100);
        entity.Property(x => x.MessageKey).HasMaxLength(100);
        entity.Property(x => x.RawMessage).IsRequired();
        entity.Property(x => x.ErrorSummary).HasMaxLength(1000).IsRequired();
        entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
        // A crash before the offset commit redelivers the message; this keeps one failure record per delivery position.
        entity.HasIndex(x => new { x.ConsumerName, x.SourceTopic, x.Partition, x.Offset }).IsUnique();
        entity.HasIndex(x => new { x.Status, x.FailedAtUtc });
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> entity)
    {
        entity.ToTable("Payments", table =>
        {
            table.HasCheckConstraint("CK_Payments_Amount", "[Amount] >= 0");
            table.HasCheckConstraint("CK_Payments_Status", "[Status] IN ('Pending','Processing','Succeeded','Failed','RefundPending','Refunded','RefundFailed')");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.Method).HasMaxLength(30).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.SettlementStatus).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        entity.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength();
        entity.Property(x => x.ProviderReference).HasMaxLength(100);
        entity.Property(x => x.ResultCode).HasMaxLength(50);
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.HasIndex(x => x.OrderId).IsUnique();
        entity.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> entity)
    {
        entity.ToTable("Refunds", table => table.HasCheckConstraint("CK_Refunds_Amount", "[Amount] >= 0"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        entity.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        entity.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength();
        entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        entity.Property(x => x.ProviderReference).HasMaxLength(100);
        entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        entity.HasIndex(x => x.IdempotencyKey).IsUnique();
        entity.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
        // The first release refunds a payment in full, so one payment has one effective refund.
        entity.HasIndex(x => x.PaymentId).IsUnique();
        entity.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> entity)
    {
        entity.ToTable("Notifications");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.Type).HasMaxLength(100).IsRequired();
        entity.Property(x => x.Channel).HasMaxLength(20).IsRequired();
        entity.Property(x => x.Text).HasMaxLength(500).IsRequired();
        entity.HasIndex(x => x.SourceEventId).IsUnique();
        entity.HasIndex(x => x.OrderId);
        entity.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
