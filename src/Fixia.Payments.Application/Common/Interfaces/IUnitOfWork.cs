namespace Fixia.Payments.Application.Common.Interfaces;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Si <paramref name="exception"/> es un choque de bloqueo optimista: otra operación cambió la misma fila
    /// (p. ej. la billetera) entre la lectura y el guardado (GC-501).
    /// </summary>
    bool IsConcurrencyConflict(Exception exception);

    /// <summary>Olvida todo lo cargado y lo pendiente, para volver a leer y reintentar desde cero (GC-501).</summary>
    void DiscardChanges();
}

