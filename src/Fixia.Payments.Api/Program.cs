using Fixia.Payments.Api.Middlewares;
using Fixia.Payments.Application.Services;
using Fixia.Payments.Infrastructure;
using Fixia.Payments.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Configuración de Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// 1. Inyección de dependencias de capas
builder.Services.AddInfrastructureServices(builder.Configuration);
builder.Services.AddScoped<IPaymentService, PaymentService>();

// 2. Controladores y Serialización JSON
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
});

builder.Services.AddHealthChecks();

var app = builder.Build();

// 3. Inicialización / Migración de Base de Datos
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        logger.LogInformation("Verificando y aplicando esquema de base de datos para ms-payments...");
        db.Database.EnsureCreated();
        logger.LogInformation("Esquema de base de datos de ms-payments verificado exitosamente.");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "No fue posible conectar o inicializar la base de datos inmediatamente. Se continuará con reintentos.");
    }
}

// 4. Pipeline HTTP
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

app.MapControllers();

app.Run();
