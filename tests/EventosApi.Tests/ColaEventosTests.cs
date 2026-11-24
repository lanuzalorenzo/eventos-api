using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventosApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventosApi.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ColaEventosTests : IAsyncLifetime
{
    private const string IdPublicacion = "evt_01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string IdConcurrencia = "evt_01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string IdLease = "evt_01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private const string IdConflicto = "evt_01ARZ3NDEKTSV4RRFFQ69G5FAY";
    private const string IdConcurrente = "evt_01ARZ3NDEKTSV4RRFFQ69G5FAZ";
    private const string IdBloqueado = "evt_01ARZ3NDEKTSV4RRFFQ69G5FB0";
    private const string IdLibre = "evt_01ARZ3NDEKTSV4RRFFQ69G5FB1";
    private const string IdReintento = "evt_01ARZ3NDEKTSV4RRFFQ69G5FB2";
    private const string IdForma = "evt_01ARZ3NDEKTSV4RRFFQ69G5FB3";

    private readonly PostgresFixture postgres;
    private readonly ApiFactoryConBase fabrica;

    public ColaEventosTests(PostgresFixture postgres)
    {
        this.postgres = postgres;
        fabrica = new ApiFactoryConBase(postgres.CadenaConexion);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        fabrica.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Dos_publicaciones_del_mismo_evento_dan_un_solo_registro()
    {
        using var cliente = NuevoCliente();

        var primera = await PublicarAsync(cliente, IdPublicacion, "prueba.publicacion", 1);
        var segunda = await PublicarAsync(cliente, IdPublicacion, "prueba.publicacion", 2);

        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);

        await using var db = postgres.Contexto();
        Assert.Equal(1, await db.Eventos.CountAsync(evento => evento.Id == IdPublicacion));

        var guardado = await db.Eventos.SingleAsync(evento => evento.Id == IdPublicacion);
        Assert.Equal(2, guardado.Carga.GetProperty("version").GetInt32());
        Assert.Equal(EstadoEvento.Pendiente, guardado.Estado);
    }

    [Fact]
    public async Task La_respuesta_del_alta_tiene_las_claves_del_contrato()
    {
        using var cliente = NuevoCliente();

        var alta = await PublicarAsync(cliente, IdForma, "prueba.forma", 1);
        var cuerpo = await alta.Content.ReadFromJsonAsync<JsonElement>();

        var claves = cuerpo.EnumerateObject().Select(propiedad => propiedad.Name).Order().ToArray();
        Assert.Equal(
            new[] { "carga", "estado", "id", "intentos", "lease_hasta", "publicado_en", "resultado", "tipo" },
            claves);
        Assert.Equal("pendiente", cuerpo.GetProperty("estado").GetString());
        Assert.Equal(JsonValueKind.Null, cuerpo.GetProperty("lease_hasta").ValueKind);
        Assert.Equal(JsonValueKind.Null, cuerpo.GetProperty("resultado").ValueKind);
    }

    [Fact]
    public async Task Dos_publicaciones_simultaneas_del_mismo_evento_dan_un_solo_registro()
    {
        using var cliente = NuevoCliente();

        var publicaciones = await Task.WhenAll(
            PublicarAsync(cliente, IdConcurrente, "prueba.publicacion.simultanea", 1),
            PublicarAsync(cliente, IdConcurrente, "prueba.publicacion.simultanea", 2));

        Assert.Equal(1, publicaciones.Count(respuesta => respuesta.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, publicaciones.Count(respuesta => respuesta.StatusCode == HttpStatusCode.OK));

        await using var db = postgres.Contexto();
        Assert.Equal(1, await db.Eventos.CountAsync(evento => evento.Id == IdConcurrente));
    }

    [Fact]
    public async Task Dos_reclamos_simultaneos_entregan_el_evento_una_vez()
    {
        using var cliente = NuevoCliente();
        var publicacion = await PublicarAsync(cliente, IdConcurrencia, "prueba.concurrencia", 1);
        Assert.Equal(HttpStatusCode.Created, publicacion.StatusCode);

        var reclamos = await Task.WhenAll(
            cliente.PostAsync("/v1/eventos/siguiente?tipo=prueba.concurrencia", content: null),
            cliente.PostAsync("/v1/eventos/siguiente?tipo=prueba.concurrencia", content: null));

        Assert.Equal(1, reclamos.Count(respuesta => respuesta.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, reclamos.Count(respuesta => respuesta.StatusCode == HttpStatusCode.NoContent));

        var entregado = reclamos.Single(respuesta => respuesta.StatusCode == HttpStatusCode.OK);
        var cuerpo = await entregado.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(IdConcurrencia, cuerpo.GetProperty("id").GetString());
        Assert.Equal(1, cuerpo.GetProperty("intentos").GetInt32());

        await using var db = postgres.Contexto();
        var guardado = await db.Eventos.SingleAsync(evento => evento.Id == IdConcurrencia);
        Assert.Equal(1, guardado.Intentos);
        Assert.Equal(EstadoEvento.Reclamado, guardado.Estado);
    }

    [Fact]
    public async Task El_lease_vencido_devuelve_el_evento_a_la_cola()
    {
        using var cliente = NuevoCliente();
        await PublicarAsync(cliente, IdLease, "prueba.lease", 1);

        var primerReclamo = await cliente.PostAsync("/v1/eventos/siguiente?tipo=prueba.lease", content: null);
        Assert.Equal(HttpStatusCode.OK, primerReclamo.StatusCode);

        int filas;
        await using (var db = postgres.Contexto())
        {
            filas = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cola.evento
                SET lease_hasta = {DateTimeOffset.UtcNow.AddSeconds(-1)}
                WHERE id = {IdLease}
                """);
        }

        Assert.Equal(1, filas);

        var segundoReclamo = await cliente.PostAsync("/v1/eventos/siguiente?tipo=prueba.lease", content: null);
        Assert.Equal(HttpStatusCode.OK, segundoReclamo.StatusCode);

        var cuerpo = await segundoReclamo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(IdLease, cuerpo.GetProperty("id").GetString());
        Assert.Equal(2, cuerpo.GetProperty("intentos").GetInt32());
    }

    [Fact]
    public async Task Republicar_un_evento_ya_reclamado_responde_conflicto()
    {
        using var cliente = NuevoCliente();
        await PublicarAsync(cliente, IdConflicto, "prueba.conflicto", 1);

        var reclamo = await cliente.PostAsync("/v1/eventos/siguiente?tipo=prueba.conflicto", content: null);
        Assert.Equal(HttpStatusCode.OK, reclamo.StatusCode);

        var republicacion = await PublicarAsync(cliente, IdConflicto, "prueba.conflicto", 2);
        Assert.Equal(HttpStatusCode.Conflict, republicacion.StatusCode);

        await using var db = postgres.Contexto();
        var guardado = await db.Eventos.SingleAsync(evento => evento.Id == IdConflicto);
        Assert.Equal(1, guardado.Carga.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Reclamar_sin_eventos_responde_sin_contenido()
    {
        using var cliente = NuevoCliente();

        var reclamo = await cliente.PostAsync("/v1/eventos/siguiente?tipo=prueba.vacia", content: null);

        Assert.Equal(HttpStatusCode.NoContent, reclamo.StatusCode);
    }

    [Fact]
    public async Task Un_tipo_invalido_responde_solicitud_invalida()
    {
        using var cliente = NuevoCliente();

        var reclamo = await cliente.PostAsync("/v1/eventos/siguiente?tipo=Cierre", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, reclamo.StatusCode);
    }

    [Fact]
    public async Task Un_evento_fallido_vuelve_a_la_cola_cuando_cumple_la_espera()
    {
        using var cliente = NuevoCliente();
        await PublicarAsync(cliente, IdReintento, "prueba.reintento", 1);

        await using (var db = postgres.Contexto())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cola.evento
                SET estado = 'fallido',
                    intentos = 1,
                    disponible_en = {DateTimeOffset.UtcNow.AddSeconds(-1)}
                WHERE id = {IdReintento}
                """);
        }

        var reclamo = await cliente.PostAsync("/v1/eventos/siguiente?tipo=prueba.reintento", content: null);
        Assert.Equal(HttpStatusCode.OK, reclamo.StatusCode);

        var cuerpo = await reclamo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(IdReintento, cuerpo.GetProperty("id").GetString());
        Assert.Equal(2, cuerpo.GetProperty("intentos").GetInt32());
    }

    [Fact]
    public async Task El_reclamo_salta_el_evento_bloqueado_por_otra_sesion()
    {
        using var cliente = NuevoCliente();
        // Si el reclamo esperase a la fila bloqueada en vez de saltarla, esta
        // petición agotaría el tiempo y la prueba fallaría.
        cliente.Timeout = TimeSpan.FromSeconds(10);

        await PublicarAsync(cliente, IdBloqueado, "prueba.bloqueo", 1);
        await PublicarAsync(cliente, IdLibre, "prueba.bloqueo", 2);

        await using var db = postgres.Contexto();
        await using var transaccion = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM cola.evento WHERE id = {IdBloqueado} FOR UPDATE");

        var reclamo = await cliente.PostAsync("/v1/eventos/siguiente?tipo=prueba.bloqueo", content: null);
        Assert.Equal(HttpStatusCode.OK, reclamo.StatusCode);

        var cuerpo = await reclamo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(IdLibre, cuerpo.GetProperty("id").GetString());

        await transaccion.RollbackAsync();
    }

    [Fact]
    public async Task Un_cuerpo_sin_identificador_responde_solicitud_invalida()
    {
        using var cliente = NuevoCliente();

        var alta = await cliente.PostAsJsonAsync("/v1/eventos", new
        {
            tipo = "prueba.sin.id",
            carga = new { }
        });

        Assert.Equal(HttpStatusCode.BadRequest, alta.StatusCode);
    }

    private HttpClient NuevoCliente() => fabrica.CreateClient();

    private static Task<HttpResponseMessage> PublicarAsync(
        HttpClient cliente,
        string id,
        string tipo,
        int version) =>
        cliente.PostAsJsonAsync("/v1/eventos", new
        {
            id,
            tipo,
            carga = new { version }
        });
}
