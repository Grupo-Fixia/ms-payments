using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fixia.Payments.Infrastructure.Persistence;

/// <summary>
/// La usan las herramientas de EF Core (<c>dotnet ef migrations add</c>, <c>dotnet ef migrations bundle</c>) para
/// construir el contexto sin arrancar la API (GC-477). Generar una migración no se conecta a la base; aplicarla con
/// <c>dotnet ef database update</c> usa <c>ConnectionStrings__PaymentsDb</c> o, si no está, la base local de DEV.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    private const string LocalConnection = "Host=localhost;Port=5433;Database=payments_db;Username=user;Password=password";

    public PaymentsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PaymentsDb") ?? LocalConnection;

        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(connectionString, b => b.MigrationsAssembly(typeof(PaymentsDbContext).Assembly.FullName))
            .Options;

        return new PaymentsDbContext(options);
    }
}
