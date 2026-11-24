using System.Text.Json;
using EventosApi.Domain;

namespace EventosApi.Api.Endpoints;

/// <summary>Representación del evento en la API, con los nombres del contrato v1.</summary>
public sealed record EventoRespuesta(
    string Id,
    string Tipo,
    JsonElement Carga,
    DateTimeOffset PublicadoEn,
    string Estado,
    int Intentos,
    DateTimeOffset? LeaseHasta,
    object? Resultado)
{
    public static EventoRespuesta Desde(Evento evento) => new(
        evento.Id,
        evento.Tipo,
        evento.Carga,
        evento.PublicadoEn,
        evento.Estado.ToString().ToLowerInvariant(),
        evento.Intentos,
        evento.LeaseHasta,
        evento.Resultado switch
        {
            null => null,
            { Hash: not null } resultado => new { hash = resultado.Hash },
            var resultado => new { motivo = resultado.Motivo }
        });
}
