using Fixia.Payments.Infrastructure.Persistence.Audit;
using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Infrastructure.Gateways;
using Fixia.Payments.Infrastructure.Messaging;
using Fixia.Payments.Infrastructure.Persistence;
using Fixia.Payments.Infrastructure.Persistence.Idempotency;
using Fixia.Payments.Infrastructure.Persistence.Inbox;
using Fixia.Payments.Infrastructure.Persistence.Outbox;
using Fixia.Payments.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fixia.Payments.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("PaymentsDb")
            ?? configuration["SPRING_DATASOURCE_URL"]?.Replace("jdbc:postgresql://", "Host=")
            ?? "Host=localhost;Port=5433;Database=payments_db;Username=user;Password=password";

        // Convertir formato jdbc si viene de variables Spring o compose legacy
        if (connectionString.StartsWith("jdbc:postgresql://"))
        {
            var raw = connectionString["jdbc:postgresql://".Length..];
            var parts = raw.Split('/');
            var hostPort = parts[0].Split(':');
            var host = hostPort[0];
            var port = hostPort.Length > 1 ? hostPort[1] : "5432";
            var db = parts.Length > 1 ? parts[1] : "payments_db";
            var user = configuration["SPRING_DATASOURCE_USERNAME"] ?? configuration["POSTGRES_USER"] ?? "user";
            var pass = configuration["SPRING_DATASOURCE_PASSWORD"] ?? configuration["POSTGRES_PASSWORD"] ?? "password";
            connectionString = $"Host={host};Port={port};Database={db};Username={user};Password={pass}";
        }

        services.AddDbContext<PaymentsDbContext>(options =>
            options.UseNpgsql(connectionString, b =>
                b.MigrationsAssembly(typeof(PaymentsDbContext).Assembly.FullName)));

        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IAdminAuditLog, EfAdminAuditLog>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IPaymentGateway, MockPaymentGateway>();

        // Billeteras simuladas y tarifas de comisión (GC-480)
        services.AddScoped<IWalletRepository, WalletRepository>();
        services.AddScoped<ICommissionRuleRepository, CommissionRuleRepository>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();

        // Outbox transaccional (GC-477): el servicio escribe el evento en la base y el relay lo publica en Kafka.
        services.AddScoped<IBusinessEventOutbox, OutboxWriter>();
        services.AddSingleton<IMessagePublisher, KafkaEventPublisher>();
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        if (configuration.GetValue($"{OutboxOptions.SectionName}:{nameof(OutboxOptions.RelayEnabled)}", true))
        {
            services.AddHostedService<OutboxRelay>();
        }

        // Eventos de ms-services (GC-484, GC-485): retener al contratar y cobrar al cerrar.
        services.AddScoped<IProcessedEventStore, EfProcessedEventStore>();
        services.Configure<ServiceEventsOptions>(configuration.GetSection(ServiceEventsOptions.SectionName));
        if (configuration.GetValue($"{ServiceEventsOptions.SectionName}:{nameof(ServiceEventsOptions.ConsumerEnabled)}", true))
        {
            services.AddHostedService<ServiceEventsConsumer>();
        }

        return services;
    }
}

