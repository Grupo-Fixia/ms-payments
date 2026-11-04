using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Fixia.Payments.Infrastructure.Persistence;

public class PaymentsDbContext : DbContext
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    public PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("payments");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

            entity.Property(e => e.ServiceRequestId)
                .HasColumnName("service_request_id")
                .IsRequired();

            entity.Property(e => e.ClientId)
                .HasColumnName("client_id")
                .IsRequired();

            entity.Property(e => e.ProviderId)
                .HasColumnName("provider_id")
                .IsRequired();

            entity.Property(e => e.Amount)
                .HasColumnName("amount")
                .HasPrecision(18, 2)
                .IsRequired();

            entity.Property(e => e.Currency)
                .HasColumnName("currency")
                .HasMaxLength(10)
                .IsRequired();

            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(30)
                .IsRequired();

            entity.Property(e => e.Method)
                .HasColumnName("method")
                .HasConversion<string>()
                .HasMaxLength(30)
                .IsRequired();

            entity.Property(e => e.TransactionReference)
                .HasColumnName("transaction_reference")
                .HasMaxLength(100);

            entity.Property(e => e.FailureReason)
                .HasColumnName("failure_reason")
                .HasMaxLength(500);

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired();

            entity.Property(e => e.UpdatedAt)
                .HasColumnName("updated_at");

            // Un solo pago por solicitud de servicio (GC-477): un reintento no puede crear un segundo cobro.
            entity.HasIndex(e => e.ServiceRequestId)
                .IsUnique()
                .HasDatabaseName("ux_payments_service_request_id");
            entity.HasIndex(e => e.ClientId).HasDatabaseName("ix_payments_client_id");

            // Bloqueo optimista con la columna de sistema xmin de PostgreSQL (GC-477): dos operaciones simultáneas
            // sobre el mismo pago no pueden confirmarse las dos; la segunda responde 409.
            entity.Property<uint>("RowVersion").IsRowVersion();

            entity.HasMany(e => e.Transactions)
                .WithOne(t => t.Payment)
                .HasForeignKey(t => t.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Navigation(e => e.Transactions)
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<PaymentTransaction>(entity =>
        {
            entity.ToTable("payment_transactions");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

            entity.Property(e => e.PaymentId)
                .HasColumnName("payment_id")
                .IsRequired();

            entity.Property(e => e.TransactionType)
                .HasColumnName("transaction_type")
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.Amount)
                .HasColumnName("amount")
                .HasPrecision(18, 2)
                .IsRequired();

            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(30)
                .IsRequired();

            entity.Property(e => e.GatewayTransactionId)
                .HasColumnName("gateway_transaction_id")
                .HasMaxLength(100);

            entity.Property(e => e.GatewayResponse)
                .HasColumnName("gateway_response");

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired();

            entity.HasIndex(e => e.PaymentId).HasDatabaseName("ix_payment_transactions_payment_id");
        });

        // Outbox transaccional (GC-477): mismas columnas e índices que el de ms-services.
        modelBuilder.Entity<OutboxEvent>(entity =>
        {
            entity.ToTable("outbox_events");

            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).HasColumnName("event_id").ValueGeneratedNever();
            entity.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(60).IsRequired();
            entity.Property(e => e.SchemaVersion).HasColumnName("schema_version").IsRequired();
            entity.Property(e => e.AggregateType).HasColumnName("aggregate_type").HasMaxLength(40).IsRequired();
            entity.Property(e => e.AggregateId).HasColumnName("aggregate_id").IsRequired();
            entity.Property(e => e.AggregateSequence).HasColumnName("aggregate_sequence").IsRequired();
            entity.Property(e => e.Topic).HasColumnName("topic").HasMaxLength(120).IsRequired();
            entity.Property(e => e.MessageKey).HasColumnName("message_key").HasMaxLength(60).IsRequired();
            entity.Property(e => e.Payload)
                .HasColumnName("payload")
                .HasMaxLength(OutboxEvent.MaxPayloadLength)
                .IsRequired();
            entity.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();
            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasConversion(
                    v => v.ToString().ToUpperInvariant(),
                    v => Enum.Parse<OutboxStatus>(v, true))
                .HasMaxLength(20)
                .IsRequired();
            entity.Property(e => e.Attempts).HasColumnName("attempts").IsRequired();
            entity.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at").IsRequired();
            entity.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(OutboxEvent.MaxErrorLength);
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");

            entity.HasIndex(e => new { e.AggregateId, e.AggregateSequence })
                .IsUnique()
                .HasDatabaseName("ux_outbox_events_aggregate_sequence");
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt })
                .HasDatabaseName("ix_outbox_events_status_next_attempt");
        });
    }
}
