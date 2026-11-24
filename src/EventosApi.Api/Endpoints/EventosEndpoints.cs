using EventosApi.Application.Eventos;
using EventosApi.Domain;

namespace EventosApi.Api.Endpoints;

public static class EventosEndpoints
{
    public static IEndpointRouteBuilder MapEventos(this IEndpointRouteBuilder rutas)
    {
        rutas.MapPost("/v1/eventos", PublicarAsync);
        rutas.MapPost("/v1/eventos/siguiente", ReclamarAsync);
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
}

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
}
