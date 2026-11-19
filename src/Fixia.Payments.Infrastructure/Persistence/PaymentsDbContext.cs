using Fixia.Payments.Domain.Entities;
using Fixia.Payments.Infrastructure.Persistence.Idempotency;
using Fixia.Payments.Infrastructure.Persistence.Inbox;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Fixia.Payments.Infrastructure.Persistence;

public class PaymentsDbContext : DbContext
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletEntry> WalletEntries => Set<WalletEntry>();
    public DbSet<CommissionRule> CommissionRules => Set<CommissionRule>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<AdminAuditEvent> AdminAuditEvents => Set<AdminAuditEvent>();

    /// <summary>Id fijo de la cuenta de Fixia sembrada por la migración de GC-480.</summary>
    public static readonly Guid PlatformWalletId = Guid.Parse("00000000-0000-0000-0000-0000000f1a10");

    /// <summary>Regla inicial aprobada por el PO en GC-476: 15 % a cargo del cliente.</summary>
    public static readonly Guid InitialCommissionRuleId = Guid.Parse("00000000-0000-0000-0000-000000000015");

    private static readonly DateTime SeedDate = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    public PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Payment>(entity =>
        {
            // GC-480: si el pago tiene desglose, el total es exactamente precio + comisión, y los dos son positivos.
            entity.ToTable("payments", t =>
            {
                t.HasCheckConstraint("ck_payments_amount_positive", "amount > 0");
                t.HasCheckConstraint("ck_payments_breakdown",
                    "(base_amount IS NULL AND commission_amount IS NULL) OR "
                    + "(base_amount > 0 AND commission_amount >= 0 AND amount = base_amount + commission_amount)");
            });

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

            entity.Property(e => e.ContractId).HasColumnName("contract_id");
            entity.Property(e => e.BaseAmount).HasColumnName("base_amount").HasPrecision(18, 2);
            entity.Property(e => e.CommissionAmount).HasColumnName("commission_amount").HasPrecision(18, 2);
            entity.Property(e => e.CommissionRate).HasColumnName("commission_rate").HasPrecision(5, 4);
            entity.Property(e => e.CommissionRuleId).HasColumnName("commission_rule_id");
            entity.HasOne<CommissionRule>()
                .WithMany()
                .HasForeignKey(e => e.CommissionRuleId)
                .HasConstraintName("fk_payments_commission_rule")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.CommissionRuleId).HasDatabaseName("ix_payments_commission_rule_id");

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

        // Billeteras simuladas (GC-480): una por dueño y moneda; los saldos nunca negativos también en la base.
        modelBuilder.Entity<Wallet>(entity =>
        {
            entity.ToTable("wallets", t =>
            {
                t.HasCheckConstraint("ck_wallets_available_non_negative", "available_balance >= 0");
                t.HasCheckConstraint("ck_wallets_held_non_negative", "held_balance >= 0");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.OwnerId).HasColumnName("owner_id").IsRequired();
            entity.Property(e => e.OwnerType)
                .HasColumnName("owner_type")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
            entity.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(10).IsRequired();
            entity.Property(e => e.AvailableBalance).HasColumnName("available_balance").HasPrecision(18, 2).IsRequired();
            entity.Property(e => e.HeldBalance).HasColumnName("held_balance").HasPrecision(18, 2).IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            // Dos operaciones simultáneas sobre la misma billetera no pueden confirmarse las dos.
            entity.Property<uint>("RowVersion").IsRowVersion();

            entity.HasIndex(e => new { e.OwnerId, e.Currency })
                .IsUnique()
                .HasDatabaseName("ux_wallets_owner_currency");

            entity.HasData(Wallet.Platform(PlatformWalletId, SeedDate));
        });

        // Libro de movimientos (GC-480): solo se agregan filas; el orden de consulta es el más reciente primero.
        modelBuilder.Entity<WalletEntry>(entity =>
        {
            entity.ToTable("wallet_entries", t =>
            {
                t.HasCheckConstraint("ck_wallet_entries_amount_positive", "amount > 0");
                t.HasCheckConstraint("ck_wallet_entries_balances_non_negative",
                    "available_after >= 0 AND held_after >= 0");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.WalletId).HasColumnName("wallet_id").IsRequired();
            entity.Property(e => e.Type)
                .HasColumnName("type")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
            entity.Property(e => e.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
            entity.Property(e => e.AvailableAfter).HasColumnName("available_after").HasPrecision(18, 2).IsRequired();
            entity.Property(e => e.HeldAfter).HasColumnName("held_after").HasPrecision(18, 2).IsRequired();
            entity.Property(e => e.PaymentId).HasColumnName("payment_id");
            entity.Property(e => e.Reference).HasColumnName("reference").HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

            entity.HasOne<Wallet>()
                .WithMany()
                .HasForeignKey(e => e.WalletId)
                .HasConstraintName("fk_wallet_entries_wallet")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Payment>()
                .WithMany()
                .HasForeignKey(e => e.PaymentId)
                .HasConstraintName("fk_wallet_entries_payment")
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.WalletId, e.CreatedAt, e.Id })
                .IsDescending(false, true, true)
                .HasDatabaseName("ix_wallet_entries_wallet_created");
            entity.HasIndex(e => e.PaymentId).HasDatabaseName("ix_wallet_entries_payment_id");
        });

        // Tarifas de comisión con vigencia (GC-480): la regla no es una constante del código.
        modelBuilder.Entity<CommissionRule>(entity =>
        {
            entity.ToTable("commission_rules", t =>
            {
                t.HasCheckConstraint("ck_commission_rules_rate", "rate > 0 AND rate < 1");
                t.HasCheckConstraint("ck_commission_rules_validity", "valid_to IS NULL OR valid_to > valid_from");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.Rate).HasColumnName("rate").HasPrecision(5, 4).IsRequired();
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from").IsRequired();
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.Description).HasColumnName("description").HasMaxLength(200).IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

            entity.HasIndex(e => e.ValidFrom).IsUnique().HasDatabaseName("ux_commission_rules_valid_from");

            entity.HasData(new CommissionRule(InitialCommissionRuleId, 0.15m, SeedDate, null,
                "Tarifa inicial aprobada por el PO (GC-476): 15 % a cargo del cliente", SeedDate));
        });

        // Claves de idempotencia (GC-482, GC-483): una por usuario, operación y clave.
        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("idempotency_keys");

            entity.HasKey(e => new { e.UserId, e.Operation, e.Key }).HasName("pk_idempotency_keys");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Operation).HasColumnName("operation").HasMaxLength(40);
            entity.Property(e => e.Key).HasColumnName("idempotency_key").HasMaxLength(100);
            entity.Property(e => e.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsRequired();
            entity.Property(e => e.ResponseJson).HasColumnName("response_json");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
        });

        // Bandeja de entrada (GC-484, GC-485): eventos de ms-services ya aplicados, por eventId.
        modelBuilder.Entity<ProcessedEvent>(entity =>
        {
            entity.ToTable("processed_events");

            entity.HasKey(e => e.EventId).HasName("pk_processed_events");
            entity.Property(e => e.EventId).HasColumnName("event_id").ValueGeneratedNever();
            entity.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(60).IsRequired();
            entity.Property(e => e.ProcessedAt).HasColumnName("processed_at").IsRequired();
        });

        // Registro de acciones del ADMIN (GC-535, contrato admin §5): solo inserción.
        modelBuilder.Entity<AdminAuditEvent>(entity =>
        {
            entity.ToTable("admin_audit_events", t =>
            {
                t.HasCheckConstraint("ck_admin_audit_events_action",
                    "action IN ('PAYMENT_LIST', 'PAYMENT_VIEW', 'PAYMENT_REFUND', 'AUDIT_LIST')");
                t.HasCheckConstraint("ck_admin_audit_events_outcome",
                    "outcome IN ('OK', 'DENIED', 'CONFLICT', 'NOT_FOUND')");
                t.HasCheckConstraint("ck_admin_audit_events_target",
                    "(target_type IS NULL AND target_id IS NULL) OR (target_type = 'PAYMENT' AND target_id IS NOT NULL)");
            });

            entity.HasKey(e => e.Id).HasName("pk_admin_audit_events");
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.AdminId).HasColumnName("admin_id").IsRequired();
            entity.Property(e => e.Action).HasColumnName("action").HasConversion<string>().HasMaxLength(30)
                .IsRequired();
            entity.Property(e => e.TargetType).HasColumnName("target_type").HasMaxLength(20);
            entity.Property(e => e.TargetId).HasColumnName("target_id");
            entity.Property(e => e.Details).HasColumnName("details");
            entity.Property(e => e.Outcome).HasColumnName("outcome").HasConversion<string>().HasMaxLength(20)
                .IsRequired();
            entity.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();

            entity.HasIndex(e => new { e.OccurredAt, e.Id })
                .IsDescending(true, true)
                .HasDatabaseName("ix_admin_audit_events_occurred");
            entity.HasIndex(e => new { e.AdminId, e.OccurredAt })
                .IsDescending(false, true)
                .HasDatabaseName("ix_admin_audit_events_admin");
        });
    }
}
