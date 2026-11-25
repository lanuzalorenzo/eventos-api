using System.Globalization;
using System.Text;
using EventosApi.Domain;

namespace EventosApi.Application.Eventos;

/// <summary>
/// Cursor opaco del listado: la clave de orden (publicado_en, id) en base64url.
/// El cliente lo devuelve tal cual y no caduca; el orden lo fija la consulta.
/// </summary>
internal static class CursorEventos
{
    public static string Codificar(Evento evento) => Codificar(evento.PublicadoEn, evento.Id);

    public static string Codificar(DateTimeOffset publicadoEn, string id) =>
        ABase64Url($"{publicadoEn.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}|{id}");

    public static (DateTimeOffset PublicadoEn, string Id)? Decodificar(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        var texto = DesdeBase64Url(cursor);
        var separador = texto.IndexOf('|', StringComparison.Ordinal);

        if (separador <= 0
            || !DateTimeOffset.TryParse(
                texto[..separador],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var publicadoEn))
        {
            throw new ArgumentException("El cursor no es válido.", nameof(cursor));
        }

        var id = texto[(separador + 1)..];
        if (!Evento.EsIdValido(id))
        {
            throw new ArgumentException("El cursor no es válido.", nameof(cursor));
        }

        return (publicadoEn, id);
    }

    private static string ABase64Url(string texto) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(texto))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string DesdeBase64Url(string cursor)
    {
        var texto = cursor.Replace('-', '+').Replace('_', '/');
        var relleno = (4 - (texto.Length % 4)) % 4;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(texto + new string('=', relleno)));
        }
        catch (FormatException)
        {
            throw new ArgumentException("El cursor no es válido.", nameof(cursor));
        }
    }
}
