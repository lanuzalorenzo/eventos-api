using EventosApi.Domain;

namespace EventosApi.Application.Eventos;

/// <summary>
/// Acceso a la cola persistida. La transacción y el bloqueo de fila son parte
/// del contrato: el alta idempotente y el reclamo necesitan leer, decidir y
/// escribir sin que otra sesión se cuele en medio.
/// </summary>
public interface IColaEventos
{
    Task<T> EnTransaccionAsync<T>(
        Func<IColaEnTransaccion, CancellationToken, Task<T>> trabajo,
        CancellationToken cancelacion);

    /// <summary>Lee el evento sin bloqueo; lo usa la consulta de lectura.</summary>
    Task<Evento?> BuscarAsync(string id, CancellationToken cancelacion);

    /// <summary>
    /// Página de eventos filtrada por estado, del más antiguo al más nuevo y
    /// ordenada por publicación e identificador. `desde` es la clave de la
    /// última fila de la página anterior; nulo para empezar.
    /// </summary>
    Task<IReadOnlyList<Evento>> ListarAsync(
        EstadoEvento? estado,
        (DateTimeOffset PublicadoEn, string Id)? desde,
        int limite,
        CancellationToken cancelacion);
}

/// <summary>Operaciones disponibles dentro de una transacción de la cola.</summary>
public interface IColaEnTransaccion
{
    /// <summary>Lee el evento con bloqueo de fila; nulo si no existe.</summary>
    Task<Evento?> BuscarConBloqueoAsync(string id, CancellationToken cancelacion);

    void Agregar(Evento evento);

    /// <summary>
    /// Devuelve el candidato más antiguo del tipo pedido, con bloqueo y sin
    /// esperar a otras sesiones. Incluye los leases vencidos para devolverlos a
    /// la cola en el mismo paso.
    /// </summary>
    Task<Evento?> SiguienteCandidatoAsync(
        string tipo,
        DateTimeOffset ahora,
        CancellationToken cancelacion);

    Task GuardarAsync(CancellationToken cancelacion);
}
