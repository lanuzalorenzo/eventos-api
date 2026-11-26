using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace EventosApi.Tests;

/// <summary>
/// Cierre del ciclo: publicar, reclamar, ack con hash, nack con espera y paso
/// a muertos tras cinco entregas. Todo contra PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CierreDelCicloTests : IAsyncLifetime
{
    private const string IdRecorrido = "evt_01ARZ3NDEKTSV4RRFFQ69G5FC0";
    private const string IdEntregaVieja = "evt_01ARZ3NDEKTSV4RRFFQ69G5FC1";
    private const string IdMuertos = "evt_01ARZ3NDEKTSV4RRFFQ69G5FC2";
    private const string IdListadoA = "evt_01ARZ3NDEKTSV4RRFFQ69G5FC3";
    private const string IdListadoB = "evt_01ARZ3NDEKTSV4RRFFQ69G5FC4";
    private const string IdListadoC = "evt_01ARZ3NDEKTSV4RRFFQ69G5FC5";
    private const string IdListadoReclamado = "evt_01ARZ3NDEKTSV4RRFFQ69G5FC6";
    private const string IdInexistente = "evt_01ARZ3NDEKTSV4RRFFQ69G5FZZ";
    private const string IdLeaseAck = "evt_01ARZ3NDEKTSV4RRFFQ69G5FC7";

    private readonly PostgresFixture postgres;
    private readonly ApiFactoryConBase fabrica;

    public CierreDelCicloTests(PostgresFixture postgres)
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
    public async Task Un_recorrido_completo_publica_reclama_ackea_y_queda_consultable()
    {
        using var cliente = NuevoCliente();

        var alta = await PublicarAsync(cliente, IdRecorrido, "prueba.cierre", 1);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var entregado = await ReclamarAsync(cliente, "prueba.cierre");
        Assert.Equal(IdRecorrido, entregado.GetProperty("id").GetString());
        var intentos = entregado.GetProperty("intentos").GetInt32();
        Assert.Equal(1, intentos);

        var ack = await cliente.PostAsJsonAsync(
            $"/v1/eventos/{IdRecorrido}/ack",
            new { hash = "sha256:0123456789abcdef", intentos });
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        var aplicado = await ack.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("aplicado", aplicado.GetProperty("estado").GetString());
        Assert.Equal(
            "sha256:0123456789abcdef",
            aplicado.GetProperty("resultado").GetProperty("hash").GetString());
        Assert.Equal(JsonValueKind.Null, aplicado.GetProperty("lease_hasta").ValueKind);

        var consulta = await cliente.GetAsync($"/v1/eventos/{IdRecorrido}");
        Assert.Equal(HttpStatusCode.OK, consulta.StatusCode);
        var consultado = await consulta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("aplicado", consultado.GetProperty("estado").GetString());

        var lista = await cliente.GetFromJsonAsync<JsonElement>("/v1/eventos?estado=aplicado&limite=200");
        Assert.Contains(
            lista.GetProperty("eventos").EnumerateArray(),
            evento => evento.GetProperty("id").GetString() == IdRecorrido);

        var segundoAck = await cliente.PostAsJsonAsync(
            $"/v1/eventos/{IdRecorrido}/ack",
            new { hash = "otro", intentos });
        Assert.Equal(HttpStatusCode.Conflict, segundoAck.StatusCode);
    }

    [Fact]
    public async Task El_ack_con_un_numero_de_entrega_que_no_es_el_vigente_responde_conflicto()
    {
        using var cliente = NuevoCliente();
        await PublicarAsync(cliente, IdEntregaVieja, "prueba.entrega", 1);

        var entregado = await ReclamarAsync(cliente, "prueba.entrega");
        var intentos = entregado.GetProperty("intentos").GetInt32();

        var vieja = await cliente.PostAsJsonAsync(
            $"/v1/eventos/{IdEntregaVieja}/ack",
            new { hash = "sha256:vieja", intentos = intentos + 1 });
        Assert.Equal(HttpStatusCode.Conflict, vieja.StatusCode);

        var vigente = await cliente.PostAsJsonAsync(
            $"/v1/eventos/{IdEntregaVieja}/ack",
            new { hash = "sha256:vigente", intentos });
        Assert.Equal(HttpStatusCode.OK, vigente.StatusCode);
    }

    [Fact]
    public async Task Cinco_nacks_llevan_el_evento_a_muertos_con_su_motivo()
    {
        using var cliente = NuevoCliente();
        await PublicarAsync(cliente, IdMuertos, "prueba.muertos", 1);

        for (var intento = 1; intento <= 5; intento++)
        {
            var entregado = await ReclamarAsync(cliente, "prueba.muertos");
            Assert.Equal(intento, entregado.GetProperty("intentos").GetInt32());

            var nack = await cliente.PostAsJsonAsync(
                $"/v1/eventos/{IdMuertos}/nack",
                new { motivo = $"fallo {intento}", intentos = intento });
            Assert.Equal(HttpStatusCode.OK, nack.StatusCode);

            var fallido = await nack.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("fallido", fallido.GetProperty("estado").GetString());
            Assert.Equal(
                $"fallo {intento}",
                fallido.GetProperty("resultado").GetProperty("motivo").GetString());
            Assert.Equal(JsonValueKind.Null, fallido.GetProperty("lease_hasta").ValueKind);

            await AdelantarEsperaAsync(IdMuertos);
        }

        var sinTrabajo = await cliente.PostAsync("/v1/eventos/siguiente?tipo=prueba.muertos", content: null);
        Assert.Equal(HttpStatusCode.NoContent, sinTrabajo.StatusCode);

        var consulta = await cliente.GetAsync($"/v1/eventos/{IdMuertos}");
        Assert.Equal(HttpStatusCode.OK, consulta.StatusCode);
        var muerto = await consulta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("muerto", muerto.GetProperty("estado").GetString());
        Assert.Equal(5, muerto.GetProperty("intentos").GetInt32());
        Assert.Equal("fallo 5", muerto.GetProperty("resultado").GetProperty("motivo").GetString());

        var nackTarde = await cliente.PostAsJsonAsync(
            $"/v1/eventos/{IdMuertos}/nack",
            new { motivo = "tarde", intentos = 5 });
        Assert.Equal(HttpStatusCode.Conflict, nackTarde.StatusCode);
    }

    [Fact]
    public async Task El_listado_pagina_por_estado_sin_repetir_ni_perder_eventos()
    {
        using var cliente = NuevoCliente();

        foreach (var (id, version) in new[]
        {
            (IdListadoA, 1),
            (IdListadoB, 2),
            (IdListadoC, 3)
        })
        {
            var alta = await PublicarAsync(cliente, id, "prueba.listado", version);
            Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        }

        var altaReclamada = await PublicarAsync(cliente, IdListadoReclamado, "prueba.listado.reclamado", 1);
        Assert.Equal(HttpStatusCode.Created, altaReclamada.StatusCode);
        await ReclamarAsync(cliente, "prueba.listado.reclamado");

        // B y C comparten el instante de publicación de A: una página de dos
        // filas corta el empate y ejerce el desempate por identificador.
        await using (var db = postgres.Contexto())
        {
            var filas = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cola.evento
                SET publicado_en = (
                    SELECT anterior.publicado_en
                    FROM cola.evento anterior
                    WHERE anterior.id = {IdListadoA})
                WHERE id = {IdListadoB} OR id = {IdListadoC}
                """);
            Assert.Equal(2, filas);
        }

        var primera = await cliente.GetFromJsonAsync<JsonElement>("/v1/eventos?estado=pendiente&limite=2");
        Assert.Equal(2, primera.GetProperty("eventos").GetArrayLength());

        var ids = new List<string>();
        foreach (var evento in primera.GetProperty("eventos").EnumerateArray())
        {
            ids.Add(evento.GetProperty("id").GetString()!);
        }

        string? cursor = primera.GetProperty("siguiente").GetString();
        Assert.NotNull(cursor);

        while (cursor is not null)
        {
            var pagina = await cliente.GetFromJsonAsync<JsonElement>(
                $"/v1/eventos?estado=pendiente&limite=2&cursor={Uri.EscapeDataString(cursor)}");

            foreach (var evento in pagina.GetProperty("eventos").EnumerateArray())
            {
                ids.Add(evento.GetProperty("id").GetString()!);
            }

            cursor = pagina.GetProperty("siguiente").GetString();
        }

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains(IdListadoA, ids);
        Assert.Contains(IdListadoB, ids);
        Assert.Contains(IdListadoC, ids);
        Assert.DoesNotContain(IdListadoReclamado, ids);
        Assert.True(ids.IndexOf(IdListadoA) < ids.IndexOf(IdListadoB));
        Assert.True(ids.IndexOf(IdListadoB) < ids.IndexOf(IdListadoC));
    }

    [Fact]
    public async Task Un_evento_inexistente_responde_no_encontrado()
    {
        using var cliente = NuevoCliente();

        var consulta = await cliente.GetAsync($"/v1/eventos/{IdInexistente}");
        Assert.Equal(HttpStatusCode.NotFound, consulta.StatusCode);
        var problema = await consulta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(404, problema.GetProperty("status").GetInt32());
        Assert.Equal("Evento no encontrado", problema.GetProperty("title").GetString());

        var ack = await cliente.PostAsJsonAsync(
            $"/v1/eventos/{IdInexistente}/ack",
            new { hash = "sha256:no-existe", intentos = 1 });
        Assert.Equal(HttpStatusCode.NotFound, ack.StatusCode);
    }

    [Fact]
    public async Task Los_parametros_fuera_del_contrato_responden_solicitud_invalida()
    {
        using var cliente = NuevoCliente();

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await cliente.GetAsync("/v1/eventos?estado=volando")).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await cliente.GetAsync("/v1/eventos?limite=201")).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await cliente.GetAsync("/v1/eventos?cursor=abc")).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await cliente.GetAsync("/v1/eventos/no-es-un-ulid")).StatusCode);
    }

    [Fact]
    public async Task Un_token_con_el_permiso_de_lectura_pasa()
    {
        using var cliente = fabrica.ConPermisos("eventos.leer");

        Assert.Equal(HttpStatusCode.OK,
            (await cliente.GetAsync("/v1/eventos")).StatusCode);
    }

    [Fact]
    public async Task Un_token_sin_el_permiso_de_lectura_recibe_403()
    {
        using var cliente = fabrica.ConPermisos("eventos.publicar", "eventos.consumir");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await cliente.GetAsync("/v1/eventos")).StatusCode);
    }

    [Fact]
    public async Task Un_token_con_roles_de_entra_mapeado_pasa()
    {
        // Entra ID entrega los permisos en `roles`; el mapeo de claims del
        // JwtBearer los convierte en ClaimTypes.Role. No se fabrica ese claim
        // directamente: se prueba el paso real.
        var emisor = fabrica.EmisorDePrueba();
        var token = emisor.CreateTokenWithPermissions(["eventos.leer"], claimType: "roles");
        using var cliente = fabrica.ConToken(token);

        Assert.Equal(HttpStatusCode.OK,
            (await cliente.GetAsync("/v1/eventos")).StatusCode);
    }

    [Fact]
    public async Task Ack_con_lease_vencido_responde_conflicto_y_solo_vale_la_entrega_vigente()
    {
        using var cliente = NuevoCliente();
        const string tipo = "prueba.lease.ack";

        var publicacion = await PublicarAsync(cliente, IdLeaseAck, tipo, 1);
        Assert.Equal(HttpStatusCode.Created, publicacion.StatusCode);

        var primera = await ReclamarAsync(cliente, tipo);
        Assert.Equal(IdLeaseAck, primera.GetProperty("id").GetString());
        Assert.Equal(1, primera.GetProperty("intentos").GetInt32());

        await VencerLeaseAsync(IdLeaseAck);

        // El lease venció: la entrega 1 ya no puede cerrar el evento.
        var ackViejo = await cliente.PostAsJsonAsync(
            $"/v1/eventos/{IdLeaseAck}/ack",
            new { hash = "sha256:viejo", intentos = 1 });
        Assert.Equal(HttpStatusCode.Conflict, ackViejo.StatusCode);

        var segunda = await ReclamarAsync(cliente, tipo);
        Assert.Equal(2, segunda.GetProperty("intentos").GetInt32());

        // Aunque el lease nuevo esté vigente, el ack de la entrega anterior no vale.
        var ackDeLaPrimera = await cliente.PostAsJsonAsync(
            $"/v1/eventos/{IdLeaseAck}/ack",
            new { hash = "sha256:primera", intentos = 1 });
        Assert.Equal(HttpStatusCode.Conflict, ackDeLaPrimera.StatusCode);

        var ackVigente = await cliente.PostAsJsonAsync(
            $"/v1/eventos/{IdLeaseAck}/ack",
            new { hash = "sha256:segunda", intentos = 2 });
        Assert.Equal(HttpStatusCode.OK, ackVigente.StatusCode);
    }

    [Fact]
    public async Task El_listado_no_repite_ni_pierde_los_eventos_que_ya_existian_mientras_se_publica()
    {
        using var cliente = NuevoCliente();
        const string tipo = "prueba.listado.concurrente";

        string[] originales =
        [
            "evt_01ARZ3NDEKTSV4RRFFQ69G5FD0",
            "evt_01ARZ3NDEKTSV4RRFFQ69G5FD1",
            "evt_01ARZ3NDEKTSV4RRFFQ69G5FD2",
            "evt_01ARZ3NDEKTSV4RRFFQ69G5FD3",
            "evt_01ARZ3NDEKTSV4RRFFQ69G5FD4",
            "evt_01ARZ3NDEKTSV4RRFFQ69G5FD5",
        ];

        foreach (var id in originales)
        {
            Assert.Equal(HttpStatusCode.Created,
                (await PublicarAsync(cliente, id, tipo, 1)).StatusCode);
        }

        var vistos = new List<string>();
        var pagina = await LeerPaginaAsync(cliente, limite: 2, cursor: null);
        AgregarIds(pagina, vistos);
        var cursor = pagina.GetProperty("siguiente").GetString();

        // Se escribe mientras el lector ya tiene su cursor: la foto no puede
        // repetir ni perder los eventos que existían al empezar.
        foreach (var id in new[]
                 {
                     "evt_01ARZ3NDEKTSV4RRFFQ69G5FD6",
                     "evt_01ARZ3NDEKTSV4RRFFQ69G5FD7",
                     "evt_01ARZ3NDEKTSV4RRFFQ69G5FD8",
                 })
        {
            Assert.Equal(HttpStatusCode.Created,
                (await PublicarAsync(cliente, id, tipo, 1)).StatusCode);
        }

        while (!string.IsNullOrEmpty(cursor))
        {
            pagina = await LeerPaginaAsync(cliente, limite: 2, cursor);
            AgregarIds(pagina, vistos);
            cursor = pagina.GetProperty("siguiente").GetString();
        }

        foreach (var id in originales)
        {
            Assert.Equal(1, vistos.Count(visto => visto == id));
        }

        Assert.Equal(vistos.Count, vistos.Distinct().Count());
    }

    private HttpClient NuevoCliente() => fabrica.ConPermisos();

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

    private static async Task<JsonElement> ReclamarAsync(HttpClient cliente, string tipo)
    {
        var respuesta = await cliente.PostAsync($"/v1/eventos/siguiente?tipo={tipo}", content: null);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        return await respuesta.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> LeerPaginaAsync(
        HttpClient cliente,
        int limite,
        string? cursor)
    {
        var ruta = $"/v1/eventos?estado=pendiente&limite={limite}";
        if (!string.IsNullOrEmpty(cursor))
        {
            ruta += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        var respuesta = await cliente.GetAsync(ruta);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        return await respuesta.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static void AgregarIds(JsonElement pagina, List<string> vistos)
    {
        foreach (var evento in pagina.GetProperty("eventos").EnumerateArray())
        {
            vistos.Add(evento.GetProperty("id").GetString()!);
        }
    }

    private async Task VencerLeaseAsync(string id)
    {
        await using var db = postgres.Contexto();
        var filas = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE cola.evento
            SET lease_hasta = {DateTimeOffset.UtcNow.AddSeconds(-1)}
            WHERE id = {id}
            """);

        Assert.Equal(1, filas);
    }

    private async Task AdelantarEsperaAsync(string id)
    {
        await using var db = postgres.Contexto();
        var filas = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE cola.evento
            SET disponible_en = {DateTimeOffset.UtcNow.AddSeconds(-1)}
            WHERE id = {id}
            """);

        Assert.Equal(1, filas);
    }
}
