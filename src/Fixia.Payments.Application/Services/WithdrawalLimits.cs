namespace Fixia.Payments.Application.Services;

/// <summary>
/// Límite de retiros simulados del técnico o empresa (GC-483). El SAD pide que los límites de retiro se administren
/// como configuración, así que vienen de la sección <c>Withdrawals</c> y no de una constante.
/// <para>
/// El día se cuenta en hora de Colombia (como las franjas de disponibilidad): a medianoche de Bogotá el cupo vuelve a
/// empezar.
/// </para>
/// </summary>
public sealed class WithdrawalLimits
{
    public const string SectionName = "Withdrawals";
    public const int DefaultMaxPerDay = 3;
    public const string DefaultTimeZoneId = "America/Bogota";

    public WithdrawalLimits(int maxPerDay = DefaultMaxPerDay, string timeZoneId = DefaultTimeZoneId)
    {
        if (maxPerDay < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPerDay), maxPerDay,
                $"{SectionName}:MaxPerDay debe ser al menos 1.");

        MaxPerDay = maxPerDay;
        TimeZone = ResolveTimeZone(timeZoneId);
    }

    /// <summary>Cuántos retiros puede hacer un mismo usuario en un día.</summary>
    public int MaxPerDay { get; }

    public TimeZoneInfo TimeZone { get; }

    /// <summary>
    /// La zona configurada. Si la imagen no trae la base de zonas horarias (p. ej. sin <c>tzdata</c>) y se pidió la de
    /// Colombia, se usa UTC−5 fijo: Colombia no tiene horario de verano, así que el resultado es el mismo.
    /// </summary>
    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException
                                   && timeZoneId == DefaultTimeZoneId)
        {
            return TimeZoneInfo.CreateCustomTimeZone(DefaultTimeZoneId, TimeSpan.FromHours(-5), "Colombia",
                "Colombia");
        }
    }

    /// <summary>El día (en la zona configurada) que contiene <paramref name="nowUtc"/>, como intervalo UTC [inicio, fin).</summary>
    public (DateTime FromUtc, DateTime ToUtc) DayOf(DateTime nowUtc)
    {
        var utc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZone).Date;
        var from = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), TimeZone);
        var to = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate.AddDays(1), DateTimeKind.Unspecified),
            TimeZone);
        return (from, to);
    }
}
