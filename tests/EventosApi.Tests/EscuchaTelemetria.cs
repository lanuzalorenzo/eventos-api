using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using EventosApi.Application.Eventos;

namespace EventosApi.Tests;

/// <summary>
/// Escucha global de trazas y métricas. Se engancha al cargar el ensamblado,
/// antes de que arranque el host, para no perder los instrumentos estáticos.
/// </summary>
internal static class EscuchaTelemetria
{
    public sealed record Actividad(
        string Nombre,
        IReadOnlyDictionary<string, string?> Etiquetas,
        string TraceId);

    public static ConcurrentQueue<Actividad> Actividades { get; } = new();

    public static ConcurrentDictionary<string, long> Contadores { get; } = new();

    [ModuleInitializer]
    public static void Iniciar()
    {
        var actividades = new ActivityListener
        {
            ShouldListenTo = fuente => fuente.Name == Telemetria.Fuente,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = actividad => Actividades.Enqueue(new Actividad(
                actividad.OperationName,
                actividad.TagObjects.ToDictionary(
                    etiqueta => etiqueta.Key,
                    etiqueta => etiqueta.Value?.ToString()),
                actividad.TraceId.ToHexString())),
        };
        ActivitySource.AddActivityListener(actividades);

        var medidor = new MeterListener
        {
            InstrumentPublished = (instrumento, escucha) =>
            {
                if (instrumento.Meter.Name == Telemetria.Fuente)
                {
                    escucha.EnableMeasurementEvents(instrumento);
                }
            },
        };
        medidor.SetMeasurementEventCallback<long>((instrumento, valor, _, _) =>
            Contadores.AddOrUpdate(instrumento.Name, valor, (_, actual) => actual + valor));
        medidor.Start();
    }
}
