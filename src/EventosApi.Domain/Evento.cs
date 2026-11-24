using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EventosApi.Domain;

public enum EstadoEvento
{
    Pendiente,
    Reclamado,
    Aplicado,
    Fallido,
    Muerto
}

public enum ResultadoReclamo
{
    Entregado,
    Apartado
}

public sealed record ResultadoEvento(string? Hash, string? Motivo);

public sealed class TransicionInvalidaException(string mensaje) : InvalidOperationException(mensaje);

/// <summary>
/// Sobre único del servicio. La carga es opaca: solo se comprueba que es un
/// objeto JSON de hasta 64 KiB. Las reglas de estado viven aquí y no dependen
/// de la base ni del host.
/// </summary>
public sealed partial class Evento
{
    public const int MaximoEntregas = 5;
    public const int MaximoBytesCarga = 64 * 1024;
    public const int MaximoCaracteresHash = 200;
    public const int MaximoCaracteresMotivo = 1000;

    public static readonly TimeSpan DuracionLease = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan EsperaMaxima = TimeSpan.FromMinutes(5);

    private Evento(string id, string tipo, JsonElement carga, DateTimeOffset publicadoEn)
    {
        Id = id;
        Tipo = tipo;
        Carga = carga;
        PublicadoEn = publicadoEn;
    }

    public string Id { get; }

    public string Tipo { get; private set; }

    public JsonElement Carga { get; private set; }

    public DateTimeOffset PublicadoEn { get; }

    public EstadoEvento Estado { get; private set; } = EstadoEvento.Pendiente;

    public int Intentos { get; private set; }

    public DateTimeOffset? LeaseHasta { get; private set; }

    /// <summary>Espera interna antes del reintento; no viaja en el sobre v1.</summary>
    internal DateTimeOffset? DisponibleEn { get; private set; }

    /// <summary>
    /// Una entrega que venció sin ack ni nack pudo dejar un consumidor vivo.
    /// La carga se congela para que no convivan dos versiones del mismo trabajo.
    /// </summary>
    internal bool EntregaAbandonada { get; private set; }

    public ResultadoEvento? Resultado { get; private set; }

    public static Evento Nuevo(string id, string tipo, JsonElement carga, DateTimeOffset publicadoEn)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (!IdValido(id))
        {
            throw new ArgumentException(
                "El identificador debe ser un ULID con el prefijo evt_ y 26 caracteres en mayúsculas.",
                nameof(id));
        }

        ValidarTipo(tipo);
        var copia = ValidarCarga(carga);

        return new Evento(id, tipo, copia, publicadoEn);
    }

    /// <summary>
    /// Reemplaza tipo y carga de un evento que todavía no se ha aplicado. El
    /// ack prueba la aplicación con el hash del commit: cambiar la carga de un
    /// evento aplicado invalidaría esa prueba.
    /// </summary>
    public void Actualizar(string tipo, JsonElement carga)
    {
        Requiere(
            Estado is EstadoEvento.Pendiente or EstadoEvento.Fallido,
            $"Solo se actualiza un evento pendiente o fallido; está {Estado}.");
        Requiere(
            !EntregaAbandonada,
            "El evento perdió una entrega sin cerrarla; la carga quedó congelada.");

        ValidarTipo(tipo);
        var copia = ValidarCarga(carga);
        Tipo = tipo;
        Carga = copia;
    }

    /// <summary>
    /// Entrega el evento al siguiente consumidor. Si ya se hicieron las cinco
    /// entregas, el reclamo no entrega: aparta el evento en muertos.
    /// </summary>
    public ResultadoReclamo Reclamar(DateTimeOffset ahora)
    {
        Requiere(Estado == EstadoEvento.Pendiente, $"No se reclama un evento en estado {Estado}.");

        if (Intentos >= MaximoEntregas)
        {
            Estado = EstadoEvento.Muerto;
            LeaseHasta = null;
            DisponibleEn = null;
            Resultado = new ResultadoEvento(null, "cinco entregas sin ack");
            return ResultadoReclamo.Apartado;
        }

        Intentos++;
        Estado = EstadoEvento.Reclamado;
        LeaseHasta = ahora + DuracionLease;
        DisponibleEn = null;
        return ResultadoReclamo.Entregado;
    }

    /// <summary>
    /// Marca el evento como aplicado. El ack debe traer el número de entrega
    /// que se está cerrando: con el lease vigente pero otra entrega, el evento
    /// pudo reclamarse otra vez y el cierre se aplicaría dos veces.
    /// </summary>
    public void Ack(string hash, int intentos, DateTimeOffset ahora)
    {
        Requiere(Estado == EstadoEvento.Reclamado, $"No se ackea un evento en estado {Estado}.");
        Requiere(LeaseVigente(ahora), "El lease venció; el evento pudo entregarse otra vez.");
        Requiere(intentos == Intentos, "La entrega no es la vigente; el evento se reclamó otra vez.");
        Requiere(
            !string.IsNullOrWhiteSpace(hash) && hash.Length <= MaximoCaracteresHash,
            "El resultado del ack debe ser el hash de un commit, de hasta 200 caracteres.");

        Estado = EstadoEvento.Aplicado;
        LeaseHasta = null;
        DisponibleEn = null;
        Resultado = new ResultadoEvento(hash, null);
    }

    /// <summary>
    /// Marca la entrega como fallida y fija la espera antes de reintentar. El
    /// nack lleva el número de entrega por la misma razón que el ack.
    /// </summary>
    public void Nack(string motivo, int intentos, DateTimeOffset ahora)
    {
        Requiere(Estado == EstadoEvento.Reclamado, $"No se nackea un evento en estado {Estado}.");
        Requiere(LeaseVigente(ahora), "El lease venció; el evento pudo entregarse otra vez.");
        Requiere(intentos == Intentos, "La entrega no es la vigente; el evento se reclamó otra vez.");
        Requiere(
            !string.IsNullOrWhiteSpace(motivo) && motivo.Length <= MaximoCaracteresMotivo,
            "El motivo del nack debe tener entre 1 y 1000 caracteres.");

        Estado = EstadoEvento.Fallido;
        LeaseHasta = null;
        DisponibleEn = ahora + EsperaReintento(Intentos);
        Resultado = new ResultadoEvento(null, motivo);
    }

    /// <summary>
    /// Devuelve a la cola un evento cuyo lease venció. No suma intentos: la
    /// entrega ya los sumó al reclamar.
    /// </summary>
    public void VencerLease(DateTimeOffset ahora)
    {
        Requiere(Estado == EstadoEvento.Reclamado, $"No se vence el lease de un evento en estado {Estado}.");
        Requiere(!LeaseVigente(ahora), "El lease sigue vigente; todavía no se puede devolver a la cola.");

        Estado = EstadoEvento.Pendiente;
        LeaseHasta = null;
        DisponibleEn = null;
        EntregaAbandonada = true;
    }

    /// <summary>
    /// Pasa un evento fallido a la cola cuando cumple la espera, o a muertos si
    /// ya agotó las cinco entregas.
    /// </summary>
    public void Disponer(DateTimeOffset ahora)
    {
        Requiere(Estado == EstadoEvento.Fallido, $"No se dispone un evento en estado {Estado}.");
        Requiere(
            DisponibleEn is { } disponible && ahora >= disponible,
            "Todavía no toca reintentar: la espera no ha terminado.");

        Estado = Intentos >= MaximoEntregas ? EstadoEvento.Muerto : EstadoEvento.Pendiente;
        DisponibleEn = null;
    }

    public static TimeSpan EsperaReintento(int intentos)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(intentos);

        var segundos = Math.Min(30L * (1L << Math.Min(intentos, MaximoEntregas)), (long)EsperaMaxima.TotalSeconds);
        return TimeSpan.FromSeconds(segundos);
    }

    private bool LeaseVigente(DateTimeOffset ahora) => LeaseHasta is { } hasta && ahora < hasta;

    private static void Requiere(bool condicion, string mensaje)
    {
        if (!condicion)
        {
            throw new TransicionInvalidaException(mensaje);
        }
    }

    /// <summary>Comprueba el tipo sin construir un evento; lo usa el reclamo por tipo.</summary>
    public static bool EsTipoValido([NotNullWhen(true)] string? tipo) =>
        !string.IsNullOrWhiteSpace(tipo) && tipo.Length <= 100 && TipoValido().IsMatch(tipo);

    private static void ValidarTipo(string tipo)
    {
        if (!EsTipoValido(tipo))
        {
            throw new ArgumentException(
                "El tipo debe ser un nombre en minúsculas separado por puntos, de hasta 100 caracteres.",
                nameof(tipo));
        }
    }

    private static JsonElement ValidarCarga(JsonElement carga)
    {
        if (carga.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("La carga debe ser un objeto JSON.", nameof(carga));
        }

        if (Encoding.UTF8.GetByteCount(carga.GetRawText()) > MaximoBytesCarga)
        {
            throw new ArgumentException("La carga supera los 64 KiB.", nameof(carga));
        }

        return carga.Clone();
    }

    private static bool IdValido(string id)
    {
        const string crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        if (id.Length != 30 || !id.StartsWith("evt_", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var caracter in id.AsSpan(4))
        {
            if (crockford.IndexOf(caracter) < 0)
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex TipoValido();
}
