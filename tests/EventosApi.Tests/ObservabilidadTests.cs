using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventosApi.Tests;

/// <summary>
/// Observabilidad de la cola: una traza por operación con el identificador del
/// evento, contadores que suben y logs que nunca copian la carga.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ObservabilidadTests : IAsyncLifetime
{
    private const string Marcador = "NO-DEBE-SALIR-EN-UN-LOG-7f3a";

    private readonly PostgresFixture postgres;
    private readonly CapturaLogs registros = new();
    private readonly ApiFactoryConBase fabrica;

    public ObservabilidadTests(PostgresFixture postgres)
    {
        this.postgres = postgres;
        fabrica = new ApiFactoryConBase(postgres.CadenaConexion, registros);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        fabrica.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task El_recorrido_publicar_reclamar_ack_deja_traza_y_metricas()
    {
        var id = NuevoId();
        var tipo = TipoPara(id);
        var antes = LeerContadores();
        using var cliente = fabrica.ConPermisos();

        var publicacion = await cliente.PostAsJsonAsync("/v1/eventos", new
        {
            id,
            tipo,
            carga = new { marcador = Marcador },
        });
        Assert.Equal(HttpStatusCode.Created, publicacion.StatusCode);

        var reclamo = await cliente.PostAsync($"/v1/eventos/siguiente?tipo={tipo}", content: null);
        Assert.Equal(HttpStatusCode.OK, reclamo.StatusCode);

        var ack = await cliente.PostAsJsonAsync(
            $"/v1/eventos/{id}/ack",
            new { hash = "sha256:observabilidad", intentos = 1 });
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        var actividades = EscuchaTelemetria.Actividades
            .Where(actividad =>
                actividad.Etiquetas.TryGetValue("evento.id", out var valor)
                && valor == id)
            .ToArray();

        Assert.Contains(actividades, actividad => actividad.Nombre == "eventos.publicar");
        Assert.Contains(actividades, actividad => actividad.Nombre == "eventos.reclamar");
        Assert.Contains(actividades, actividad => actividad.Nombre == "eventos.ack");
        Assert.All(actividades, actividad => Assert.False(string.IsNullOrEmpty(actividad.TraceId)));

        var despues = LeerContadores();
        Assert.Equal(
            1,
            despues.GetValueOrDefault("eventos.publicados") - antes.GetValueOrDefault("eventos.publicados"));
        Assert.Equal(
            1,
            despues.GetValueOrDefault("eventos.reclamados") - antes.GetValueOrDefault("eventos.reclamados"));
        Assert.Equal(
            1,
            despues.GetValueOrDefault("eventos.aplicados") - antes.GetValueOrDefault("eventos.aplicados"));
        Assert.Equal(
            0,
            despues.GetValueOrDefault("eventos.fallidos") - antes.GetValueOrDefault("eventos.fallidos"));
        Assert.Equal(
            0,
            despues.GetValueOrDefault("eventos.muertos") - antes.GetValueOrDefault("eventos.muertos"));
    }

    [Fact]
    public async Task El_paso_a_muerto_cuenta_y_deja_log_sin_carga()
    {
        var id = NuevoId();
        var tipo = TipoPara(id);
        using var cliente = fabrica.ConPermisos();

        var publicacion = await cliente.PostAsJsonAsync("/v1/eventos", new
        {
            id,
            tipo,
            carga = new { marcador = Marcador },
        });
        Assert.Equal(HttpStatusCode.Created, publicacion.StatusCode);

        // Quinta entrega con el lease vencido: el siguiente reclamo lo aparta.
        await using (var db = postgres.Contexto())
        {
            var filas = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cola.evento
                SET estado = 'reclamado',
                    intentos = 5,
                    lease_hasta = {DateTimeOffset.UtcNow.AddSeconds(-1)}
                WHERE id = {id}
                """);
            Assert.Equal(1, filas);
        }

        var antes = LeerContadores();
        var reclamo = await cliente.PostAsync($"/v1/eventos/siguiente?tipo={tipo}", content: null);
        Assert.Equal(HttpStatusCode.NoContent, reclamo.StatusCode);
        var despues = LeerContadores();

        Assert.Equal(
            1,
            despues.GetValueOrDefault("eventos.muertos") - antes.GetValueOrDefault("eventos.muertos"));
        Assert.Contains(
            registros.Mensajes,
            mensaje => mensaje.Contains(id, StringComparison.Ordinal)
                && mensaje.Contains("apartado en muerto", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Los_logs_no_incluyen_la_carga()
    {
        var id = NuevoId();
        var tipo = TipoPara(id);
        using var cliente = fabrica.ConPermisos();

        var publicacion = await cliente.PostAsJsonAsync("/v1/eventos", new
        {
            id,
            tipo,
            carga = new { marcador = Marcador },
        });
        Assert.Equal(HttpStatusCode.Created, publicacion.StatusCode);

        var mensajes = registros.Mensajes.ToArray();

        Assert.DoesNotContain(
            mensajes,
            mensaje => mensaje.Contains(Marcador, StringComparison.Ordinal));
        Assert.Contains(
            mensajes,
            mensaje => mensaje.Contains(id, StringComparison.Ordinal)
                && mensaje.Contains("publicado", StringComparison.OrdinalIgnoreCase));

        // Defensa en profundidad: el SQL de EF no debe copiar el jsonb.
        var configuracion = fabrica.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(
            "Warning",
            configuracion["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"]);
    }

    private Dictionary<string, long> LeerContadores() =>
        new(EscuchaTelemetria.Contadores);

    private static string NuevoId() =>
        "evt_" + Guid.NewGuid().ToString("N").ToUpperInvariant()[..26];

    private static string TipoPara(string id) =>
        "prueba.observabilidad." + id[^8..].ToLowerInvariant();
}
