using Fixia.Payments.Api.Middlewares;
using Fixia.Payments.Api.Security;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Infrastructure;
using Fixia.Payments.Infrastructure.Persistence;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Configuración de Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// 1. Inyección de dependencias de capas
builder.Services.AddInfrastructureServices(builder.Configuration);
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<ICommissionService, CommissionService>();
builder.Services.AddScoped<IWalletOperationsService, WalletOperationsService>();
builder.Services.AddScoped<IPaymentRefundService, PaymentRefundService>();

// 2. Seguridad: token de ms-users en /api/payments y clave interna en /internal (GC-477)
builder.Services.AddFixiaSecurity(builder.Configuration, builder.Environment);

// 3. Controladores y Serialización JSON
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Fixia Payments API (.NET 8)",
        Version = "v1",
        Description = "Microservicio de gestión y procesamiento de pagos de Fixia en .NET 8."
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Access token de ms-users (POST /api/users/auth/login)."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddHealthChecks();

var app = builder.Build();

// 4. Migraciones versionadas de EF Core (GC-477): reemplazan a EnsureCreated()
await DatabaseMigrator.ApplyMigrationsAsync(app.Services, app.Configuration,
    app.Services.GetRequiredService<ILogger<Program>>());

// 5. Pipeline HTTP
app.UseMiddleware<GlobalExceptionMiddleware>();

// Swagger habilitado tanto en desarrollo como a través de API Gateway
app.UseSwagger(c =>
{
    c.RouteTemplate = "api/payments/swagger/{documentName}/swagger.json";
});
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/api/payments/swagger/v1/swagger.json", "Fixia Payments API v1");
    c.RoutePrefix = "api/payments/swagger";
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

/// <summary>Visible para las pruebas de integración (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program
{
}
