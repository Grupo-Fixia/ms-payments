using System.Text;
using Fixia.Payments.Application.Common.Interfaces;
using Fixia.Payments.Domain.Exceptions;

namespace Fixia.Payments.Application.Services;

/// <summary>
/// Cursor opaco de la lista de movimientos (GC-481): codifica la fecha y el id del último movimiento entregado, como el
/// cursor del historial de ms-services. Uno manipulado solo cambia el punto de partida, nunca de quién es la billetera,
/// que sale del token.
/// </summary>
public static class WalletEntryCursor
{
    public static string Encode(WalletEntryPosition position)
    {
        var raw = $"{position.CreatedAt.Ticks}:{position.Id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <exception cref="InvalidQueryException">Si el texto no salió de esta API.</exception>
    public static WalletEntryPosition Decode(string cursor)
    {
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split(':');
            if (parts.Length == 2
                && long.TryParse(parts[0], out var ticks)
                && ticks > DateTime.MinValue.Ticks && ticks < DateTime.MaxValue.Ticks
                && Guid.TryParseExact(parts[1], "N", out var id))
            {
                return new WalletEntryPosition(new DateTime(ticks, DateTimeKind.Utc), id);
            }
        }
        catch (FormatException)
        {
            // cae al error común
        }

        throw new InvalidQueryException("El cursor no es válido.");
    }
}
