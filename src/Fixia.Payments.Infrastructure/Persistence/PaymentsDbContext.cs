using Fixia.Payments.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fixia.Payments.Infrastructure.Persistence;

public class PaymentsDbContext : DbContext
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();

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

            entity.HasIndex(e => e.ServiceRequestId).HasDatabaseName("ix_payments_service_request_id");
            entity.HasIndex(e => e.ClientId).HasDatabaseName("ix_payments_client_id");

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
    }
}
