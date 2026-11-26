using System.Text.Json;
using EventosApi.Domain;
using Microsoft.Extensions.Logging;

namespace EventosApi.Application.Eventos;

/// <summary>Cuerpo de la publicación: el sobre del contrato v1.</summary>
public sealed record PublicarEvento(string Id, string Tipo, JsonElement Carga);

/// <summary>Resultado del alta: el evento y si se creó o se actualizó.</summary>
public sealed record ResultadoPublicacion(Evento Evento, bool Creado);

/// <summary>Cierre de una entrega con el hash del commit que la aplicó.</summary>
public sealed record SolicitudAck(string Hash, int Intentos);

/// <summary>Cierre fallido de una entrega, con el motivo y la espera de reintento.</summary>
public sealed record SolicitudNack(string Motivo, int Intentos);

/// <summary>Página del listado con el cursor de la siguiente, o nulo al final.</summary>
public sealed record PaginaEventos(IReadOnlyList<Evento> Eventos, string? Siguiente);

/// <summary>
/// Casos de uso de la cola. El alta es idempotente por identificador y el
/// reclamo entrega un solo evento por sesión, con lease.
/// </summary>
public sealed class ServicioEventos(
    IColaEventos cola,
    TimeProvider reloj,
    ILogger<ServicioEventos> registro)
{
    private const int IntentosDeAlta = 2;
    private const int LimitePorDefecto = 50;
    private const int LimiteMaximo = 200;

    public async Task<ResultadoPublicacion> PublicarAsync(
        PublicarEvento solicitud,
        CancellationToken cancelacion)
    {
        ArgumentNullException.ThrowIfNull(solicitud);

        using var actividad = Telemetria.Actividades.StartActivity("eventos.publicar");

        var resultado = await PublicarConReintentoAsync(solicitud, cancelacion);

        actividad?.SetTag("evento.id", resultado.Evento.Id);
        actividad?.SetTag("evento.tipo", resultado.Evento.Tipo);
        actividad?.SetTag("evento.estado", resultado.Evento.Estado.ToString().ToLowerInvariant());
        actividad?.SetTag("evento.creado", resultado.Creado);
        Telemetria.Publicados.Add(1);
        registro.LogInformation(
            "Evento {EventoId} publicado en estado {Estado} ({Resultado})",
            resultado.Evento.Id,
            resultado.Evento.Estado,
            resultado.Creado ? "creado" : "actualizado");

        return resultado;
    }

    public async Task<Evento?> ReclamarAsync(string? tipo, CancellationToken cancelacion)
    {
        if (!Evento.EsTipoValido(tipo))
        {
            throw new ArgumentException(
                "El tipo debe ser un nombre en minúsculas separado por puntos, de hasta 100 caracteres.",
                nameof(tipo));
        }

        using var actividad = Telemetria.Actividades.StartActivity("eventos.reclamar");
        actividad?.SetTag("evento.tipo", tipo);

        var ahora = reloj.GetUtcNow();
        var apartados = new List<Evento>();

        var evento = await cola.EnTransaccionAsync(async (transaccion, ct) =>
        {
            // Un evento puede apartarse en muertos al reclamarlo; se sigue con
            // el siguiente candidato en la misma transacción. Cada vuelta deja
            // un evento fuera de la cola o entrega uno, así que el bucle ter-
            // mina cuando no quedan candidatos.
            while (true)
            {
                var candidato = await transaccion.SiguienteCandidatoAsync(tipo, ahora, ct);
                if (candidato is null)
                {
                    return null;
                }

                if (candidato.Estado == EstadoEvento.Reclamado)
                {
                    candidato.VencerLease(ahora);
                }
                else if (candidato.Estado == EstadoEvento.Fallido)
                {
                    candidato.Disponer(ahora);
                }

                if (candidato.Estado == EstadoEvento.Muerto)
                {
                    apartados.Add(candidato);
                    await transaccion.GuardarAsync(ct);
                    continue;
                }

                var resultado = candidato.Reclamar(ahora);
                await transaccion.GuardarAsync(ct);

                if (resultado == ResultadoReclamo.Apartado)
                {
                    apartados.Add(candidato);
                    continue;
                }

                if (resultado == ResultadoReclamo.Entregado)
                {
                    return candidato;
                }
            }
        }, cancelacion);

        foreach (var apartado in apartados)
        {
            Telemetria.Muertos.Add(1);
            registro.LogWarning("Evento {EventoId} apartado en muerto", apartado.Id);
        }

        if (evento is null)
        {
            actividad?.SetTag("evento.encontrado", false);
            return null;
        }

        actividad?.SetTag("evento.id", evento.Id);
        actividad?.SetTag("evento.intentos", evento.Intentos);
        Telemetria.Reclamados.Add(1);
        registro.LogInformation(
            "Evento {EventoId} reclamado en la entrega {Intentos}",
            evento.Id,
            evento.Intentos);
        return evento;
    }

    public async Task<PaginaEventos> ListarAsync(
        string? estado,
        int? limite,
        string? cursor,
        CancellationToken cancelacion)
    {
        using var actividad = Telemetria.Actividades.StartActivity("eventos.listar");

        var tamano = limite ?? LimitePorDefecto;

        if (tamano is < 1 or > LimiteMaximo)
        {
            throw new ArgumentException(
                $"El límite debe estar entre 1 y {LimiteMaximo}.",
                nameof(limite));
        }

        var filtro = EstadoPedido(estado);
        var desde = CursorEventos.Decodificar(cursor);

        // Se pide una fila de más para saber si queda página siguiente.
        var eventos = await cola.ListarAsync(filtro, desde, tamano + 1, cancelacion);
        var pagina = eventos.Take(tamano).ToArray();

        return new PaginaEventos(
            pagina,
            eventos.Count > tamano ? CursorEventos.Codificar(pagina[^1]) : null);
    }

    public async Task<Evento?> ConsultarAsync(string id, CancellationToken cancelacion)
    {
        ValidarId(id);
        using var actividad = Telemetria.Actividades.StartActivity("eventos.consultar");
        actividad?.SetTag("evento.id", id);
        return await cola.BuscarAsync(id, cancelacion);
    }

    /// <summary>
    /// Cierra la entrega con el hash del commit. Devuelve nulo si el evento no
    /// existe; una entrega anterior o un lease vencido lanzan la transición.
    /// </summary>
    public async Task<Evento?> AcusarAsync(
        string id,
        SolicitudAck solicitud,
        CancellationToken cancelacion)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        ValidarId(id);

        if (string.IsNullOrWhiteSpace(solicitud.Hash)
            || solicitud.Hash.Length > Evento.MaximoCaracteresHash)
        {
            throw new ArgumentException(
                "El hash del ack debe tener entre 1 y 200 caracteres.",
                nameof(solicitud));
        }

        if (solicitud.Intentos is < 1 or > Evento.MaximoEntregas)
        {
            throw new ArgumentException(
                $"El número de entrega debe estar entre 1 y {Evento.MaximoEntregas}.",
                nameof(solicitud));
        }

        using var actividad = Telemetria.Actividades.StartActivity("eventos.ack");
        actividad?.SetTag("evento.id", id);

        var evento = await cola.EnTransaccionAsync(async (transaccion, ct) =>
        {
            var evento = await transaccion.BuscarConBloqueoAsync(id, ct);
            if (evento is null)
            {
                return null;
            }

            evento.Ack(solicitud.Hash, solicitud.Intentos, reloj.GetUtcNow());
            await transaccion.GuardarAsync(ct);
            return evento;
        }, cancelacion);

        if (evento is not null)
        {
            Telemetria.Aplicados.Add(1);
            registro.LogInformation("Evento {EventoId} aplicado", evento.Id);
        }

        return evento;
    }

    /// <summary>
    /// Marca la entrega como fallida y fija la espera antes del reintento.
    /// Devuelve nulo si el evento no existe.
    /// </summary>
    public async Task<Evento?> RechazarAsync(
        string id,
        SolicitudNack solicitud,
        CancellationToken cancelacion)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        ValidarId(id);

        if (string.IsNullOrWhiteSpace(solicitud.Motivo)
            || solicitud.Motivo.Length > Evento.MaximoCaracteresMotivo)
        {
            throw new ArgumentException(
                "El motivo del nack debe tener entre 1 y 1000 caracteres.",
                nameof(solicitud));
        }

        if (solicitud.Intentos is < 1 or > Evento.MaximoEntregas)
        {
            throw new ArgumentException(
                $"El número de entrega debe estar entre 1 y {Evento.MaximoEntregas}.",
                nameof(solicitud));
        }

        using var actividad = Telemetria.Actividades.StartActivity("eventos.nack");
        actividad?.SetTag("evento.id", id);

        var evento = await cola.EnTransaccionAsync(async (transaccion, ct) =>
        {
            var evento = await transaccion.BuscarConBloqueoAsync(id, ct);
            if (evento is null)
            {
                return null;
            }

            evento.Nack(solicitud.Motivo, solicitud.Intentos, reloj.GetUtcNow());
            await transaccion.GuardarAsync(ct);
            return evento;
        }, cancelacion);

        if (evento is not null)
        {
            Telemetria.Fallidos.Add(1);
            registro.LogInformation("Evento {EventoId} fallido; reintento programado", evento.Id);
        }

        return evento;
    }

    private static EstadoEvento? EstadoPedido(string? estado)
    {
        if (string.IsNullOrWhiteSpace(estado))
        {
            return null;
        }

        var nombre = Enum.GetNames<EstadoEvento>()
            .FirstOrDefault(candidato =>
                string.Equals(candidato, estado, StringComparison.OrdinalIgnoreCase));

        return nombre is null
            ? throw new ArgumentException(
                "El estado debe ser pendiente, reclamado, aplicado, fallido o muerto.",
                nameof(estado))
            : Enum.Parse<EstadoEvento>(nombre);
    }

    private static void ValidarId(string id)
    {
        if (!Evento.EsIdValido(id))
        {
            throw new ArgumentException(
                "El identificador debe ser un ULID con el prefijo evt_ y 26 caracteres en mayúsculas.",
                nameof(id));
        }
    }

    private async Task<ResultadoPublicacion> PublicarConReintentoAsync(
        PublicarEvento solicitud,
        CancellationToken cancelacion)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                return await cola.EnTransaccionAsync(async (transaccion, ct) =>
                {
                    var existente = await transaccion.BuscarConBloqueoAsync(solicitud.Id, ct);

                    if (existente is null)
                    {
                        var nuevo = Evento.Nuevo(
                            solicitud.Id,
                            solicitud.Tipo,
                            solicitud.Carga,
                            reloj.GetUtcNow());
                        transaccion.Agregar(nuevo);
                        await transaccion.GuardarAsync(ct);
                        return new ResultadoPublicacion(nuevo, Creado: true);
                    }

                    existente.Actualizar(solicitud.Tipo, solicitud.Carga);
                    await transaccion.GuardarAsync(ct);
                    return new ResultadoPublicacion(existente, Creado: false);
                }, cancelacion);
            }
            catch (ConflictoDeUnicidadException) when (intento < IntentosDeAlta)
            {
                // Otra sesión ganó el alta con el mismo id: al reintentar ya
                // existe y el camino de actualización decide si se admite.
            }
        }
    }
}
