using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EventosApi.Application.Eventos;

/// <summary>
/// Nombres y medidores de la cola. Una sola fuente para las trazas y las
/// métricas; el paquete compartido de observabilidad los recogerá cuando
/// exista.
/// </summary>
public static class Telemetria
{
    public const string Fuente = "EventosApi";

    public static readonly ActivitySource Actividades = new(Fuente);
    public static readonly Meter Medidor = new(Fuente);

    public static readonly Counter<long> Publicados = Medidor.CreateCounter<long>("eventos.publicados");
    public static readonly Counter<long> Reclamados = Medidor.CreateCounter<long>("eventos.reclamados");
    public static readonly Counter<long> Aplicados = Medidor.CreateCounter<long>("eventos.aplicados");
    public static readonly Counter<long> Fallidos = Medidor.CreateCounter<long>("eventos.fallidos");
    public static readonly Counter<long> Muertos = Medidor.CreateCounter<long>("eventos.muertos");
}
