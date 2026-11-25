using EventosApi.Application.Eventos;
using EventosApi.Domain;

namespace EventosApi.Api.Endpoints;

public static class EventosEndpoints
{
    public static IEndpointRouteBuilder MapEventos(this IEndpointRouteBuilder rutas)
    {
        rutas.MapPost("/v1/eventos", PublicarAsync);
        rutas.MapGet("/v1/eventos", ListarAsync);
        rutas.MapPost("/v1/eventos/siguiente", ReclamarAsync);
        rutas.MapGet("/v1/eventos/{id}", ConsultarAsync);
        rutas.MapPost("/v1/eventos/{id}/ack", AcusarAsync);
        rutas.MapPost("/v1/eventos/{id}/nack", RechazarAsync);
        return rutas;
    }

    private static async Task<IResult> PublicarAsync(
        PublicarEvento solicitud,
        ServicioEventos servicio,
        CancellationToken cancelacion)
    {
        try
        {
            var resultado = await servicio.PublicarAsync(solicitud, cancelacion);
            var cuerpo = EventoRespuesta.Desde(resultado.Evento);

            return resultado.Creado
                ? Results.Created($"/v1/eventos/{resultado.Evento.Id}", cuerpo)
                : Results.Ok(cuerpo);
        }
        catch (ArgumentException excepcion)
        {
            return Problemas.Invalida(excepcion.Message);
        }
        catch (TransicionInvalidaException excepcion)
        {
            return Problemas.Conflicto(excepcion.Message);
        }
    }

    private static async Task<IResult> ReclamarAsync(
        string? tipo,
        ServicioEventos servicio,
        CancellationToken cancelacion)
    {
        try
        {
            var evento = await servicio.ReclamarAsync(tipo, cancelacion);

            return evento is null
                ? Results.NoContent()
                : Results.Ok(EventoRespuesta.Desde(evento));
        }
        catch (ArgumentException excepcion)
        {
            return Problemas.Invalida(excepcion.Message);
        }
        catch (TransicionInvalidaException excepcion)
        {
            return Problemas.Conflicto(excepcion.Message);
        }
    }

    private static async Task<IResult> ListarAsync(
        string? estado,
        int? limite,
        string? cursor,
        ServicioEventos servicio,
        CancellationToken cancelacion)
    {
        try
        {
            var pagina = await servicio.ListarAsync(estado, limite, cursor, cancelacion);

            return Results.Ok(new PaginaEventosRespuesta(
                pagina.Eventos.Select(EventoRespuesta.Desde).ToArray(),
                pagina.Siguiente));
        }
        catch (ArgumentException excepcion)
        {
            return Problemas.Invalida(excepcion.Message);
        }
    }

    private static async Task<IResult> ConsultarAsync(
        string id,
        ServicioEventos servicio,
        CancellationToken cancelacion)
    {
        try
        {
            var evento = await servicio.ConsultarAsync(id, cancelacion);

            return evento is null
                ? Problemas.NoEncontrado(id)
                : Results.Ok(EventoRespuesta.Desde(evento));
        }
        catch (ArgumentException excepcion)
        {
            return Problemas.Invalida(excepcion.Message);
        }
    }

    private static async Task<IResult> AcusarAsync(
        string id,
        SolicitudAck solicitud,
        ServicioEventos servicio,
        CancellationToken cancelacion)
    {
        try
        {
            var evento = await servicio.AcusarAsync(id, solicitud, cancelacion);

            return evento is null
                ? Problemas.NoEncontrado(id)
                : Results.Ok(EventoRespuesta.Desde(evento));
        }
        catch (ArgumentException excepcion)
        {
            return Problemas.Invalida(excepcion.Message);
        }
        catch (TransicionInvalidaException excepcion)
        {
            return Problemas.Conflicto(excepcion.Message);
        }
    }

    private static async Task<IResult> RechazarAsync(
        string id,
        SolicitudNack solicitud,
        ServicioEventos servicio,
        CancellationToken cancelacion)
    {
        try
        {
            var evento = await servicio.RechazarAsync(id, solicitud, cancelacion);

            return evento is null
                ? Problemas.NoEncontrado(id)
                : Results.Ok(EventoRespuesta.Desde(evento));
        }
        catch (ArgumentException excepcion)
        {
            return Problemas.Invalida(excepcion.Message);
        }
        catch (TransicionInvalidaException excepcion)
        {
            return Problemas.Conflicto(excepcion.Message);
        }
    }
}

/// <summary>Página del listado con los nombres del contrato v1.</summary>
public sealed record PaginaEventosRespuesta(
    IReadOnlyList<EventoRespuesta> Eventos,
    string? Siguiente);

internal static class Problemas
{
    public static IResult Invalida(string detalle) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Solicitud inválida",
        detail: detalle);

    public static IResult Conflicto(string detalle) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "El estado no admite la operación",
        detail: detalle);

    public static IResult NoEncontrado(string id) => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Evento no encontrado",
        detail: $"No existe un evento con el identificador {id}.");
}
