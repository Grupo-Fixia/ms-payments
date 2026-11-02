namespace Fixia.Payments.Infrastructure.Messaging;

/// <summary>Configuración del relay del Outbox (sección <c>Outbox</c> de la configuración).</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Si el relay corre en este proceso. Las pruebas lo apagan.</summary>
    public bool RelayEnabled { get; set; } = true;

    /// <summary>Espera entre rondas cuando no hubo nada que publicar.</summary>
    public int PollIntervalMilliseconds { get; set; } = 2000;

    /// <summary>Eventos que toma cada ronda.</summary>
    public int BatchSize { get; set; } = 50;
}
