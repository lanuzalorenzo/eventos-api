using System.Text.Json;
using EventosApi.Domain;

namespace EventosApi.Application.Eventos;

/// <summary>Cuerpo de la publicación: el sobre del contrato v1.</summary>
public sealed record PublicarEvento(string Id, string Tipo, JsonElement Carga);

/// <summary>Resultado del alta: el evento y si se creó o se actualizó.</summary>
public sealed record ResultadoPublicacion(Evento Evento, bool Creado);

/// <summary>
/// Casos de uso de la cola. El alta es idempotente por identificador y el
/// reclamo entrega un solo evento por sesión, con lease.
/// </summary>
public sealed class ServicioEventos(IColaEventos cola, TimeProvider reloj)
{
    private const int IntentosDeAlta = 2;

    public Task<ResultadoPublicacion> PublicarAsync(
        PublicarEvento solicitud,
        CancellationToken cancelacion)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        return PublicarConReintentoAsync(solicitud, cancelacion);
    }

    public async Task<Evento?> ReclamarAsync(string? tipo, CancellationToken cancelacion)
    {
        if (!Evento.EsTipoValido(tipo))
        {
            throw new ArgumentException(
                "El tipo debe ser un nombre en minúsculas separado por puntos, de hasta 100 caracteres.",
                nameof(tipo));
        }

        var ahora = reloj.GetUtcNow();

        return await cola.EnTransaccionAsync(async (transaccion, ct) =>
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
                    await transaccion.GuardarAsync(ct);
                    continue;
                }

                var resultado = candidato.Reclamar(ahora);
                await transaccion.GuardarAsync(ct);

                if (resultado == ResultadoReclamo.Entregado)
                {
                    return candidato;
                }
            }
        }, cancelacion);
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
