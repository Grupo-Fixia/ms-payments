using Fixia.Payments.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>
/// Las migraciones son la única forma de crear el esquema (GC-477). Si alguien cambia el modelo y olvida generar la
/// migración, esta prueba falla. No necesita base de datos: compara el modelo con la última migración.
/// </summary>
public class MigrationsTests
{
    private static PaymentsDbContext NpgsqlContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql("Host=localhost;Database=no_se_conecta")
            .Options);

    [Fact]
    public void Model_ShouldHaveNoChangesPendingOfAMigration()
    {
        using var db = NpgsqlContext();

        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        snapshot.Should().NotBeNull("debe existir al menos una migración");

        var snapshotModel = snapshot!.Model;
        if (snapshotModel is IMutableModel mutable)
            snapshotModel = mutable.FinalizeModel();
        snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel);

        var differ = db.GetService<IMigrationsModelDiffer>();
        var differences = differ.GetDifferences(
            snapshotModel.GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        differences.Should().BeEmpty("cada cambio del modelo necesita su migración (dotnet ef migrations add)");
    }

    [Fact]
    public void Migrations_ShouldStartWithTheInitialSchema()
    {
        using var db = NpgsqlContext();

        db.Database.GetMigrations().Should().NotBeEmpty()
            .And.Subject.First().Should().EndWith("_InitialCreate");
    }
}
