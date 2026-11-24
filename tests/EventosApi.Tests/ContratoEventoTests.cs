using System.Text.Json;
using EventosApi.Domain;

namespace EventosApi.Tests;

public sealed class ContratoEventoTests
{
    private const string Id = "evt_01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Tipo = "cierre.sesion";

    private static readonly DateTimeOffset Ahora = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nuevo_arranca_pendiente_sin_intentos()
    {
        var evento = Nuevo();

        Assert.Equal(EstadoEvento.Pendiente, evento.Estado);
        Assert.Equal(0, evento.Intentos);
        Assert.Null(evento.LeaseHasta);
        Assert.Null(evento.Resultado);
    }

    [Fact]
    public void Reclamar_entrega_con_lease_de_cinco_minutos()
    {
        var evento = Nuevo();

        var resultado = evento.Reclamar(Ahora);

        Assert.Equal(ResultadoReclamo.Entregado, resultado);
        Assert.Equal(EstadoEvento.Reclamado, evento.Estado);
        Assert.Equal(1, evento.Intentos);
        Assert.Equal(Ahora + TimeSpan.FromSeconds(300), evento.LeaseHasta);
    }

    [Fact]
    public void Reclamar_dos_veces_es_prohibido()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);

        Assert.Throws<TransicionInvalidaException>(() => evento.Reclamar(Ahora));
    }

    [Fact]
    public void Ack_marca_aplicado_con_el_hash()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);

        evento.Ack("0123456789abcdef", 1, Ahora + TimeSpan.FromMinutes(1));

        Assert.Equal(EstadoEvento.Aplicado, evento.Estado);
        Assert.Null(evento.LeaseHasta);
        Assert.Equal("0123456789abcdef", evento.Resultado?.Hash);
        Assert.Null(evento.Resultado?.Motivo);
    }

    [Fact]
    public void Ack_sin_reclamo_es_prohibido()
    {
        var evento = Nuevo();

        Assert.Throws<TransicionInvalidaException>(() => evento.Ack("hash", 0, Ahora));
    }

    [Fact]
    public void Ack_con_lease_vencido_es_prohibido()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);

        Assert.Throws<TransicionInvalidaException>(() =>
            evento.Ack("hash", 1, Ahora + TimeSpan.FromSeconds(300)));
    }

    [Fact]
    public void El_ack_de_una_entrega_anterior_se_rechaza_aunque_el_lease_este_vigente()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);
        var segundoReclamo = Ahora + TimeSpan.FromSeconds(300);
        evento.VencerLease(segundoReclamo);
        evento.Reclamar(segundoReclamo);

        Assert.Throws<TransicionInvalidaException>(() =>
            evento.Ack("hash-de-la-primera-entrega", 1, segundoReclamo + TimeSpan.FromMinutes(1)));

        evento.Ack("hash-de-la-segunda-entrega", 2, segundoReclamo + TimeSpan.FromMinutes(1));
        Assert.Equal(EstadoEvento.Aplicado, evento.Estado);
    }

    [Fact]
    public void Nack_marca_fallido_y_fija_la_espera()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);
        var momento = Ahora + TimeSpan.FromMinutes(1);

        evento.Nack("la base no responde", 1, momento);

        Assert.Equal(EstadoEvento.Fallido, evento.Estado);
        Assert.Null(evento.LeaseHasta);
        Assert.Equal(momento + TimeSpan.FromSeconds(60), evento.DisponibleEn);
        Assert.Equal("la base no responde", evento.Resultado?.Motivo);
    }

    [Fact]
    public void Nack_sin_reclamo_es_prohibido()
    {
        var evento = Nuevo();

        Assert.Throws<TransicionInvalidaException>(() => evento.Nack("motivo", 0, Ahora));
    }

    [Fact]
    public void Nack_con_lease_vencido_es_prohibido()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);

        Assert.Throws<TransicionInvalidaException>(() =>
            evento.Nack("motivo", 1, Ahora + TimeSpan.FromSeconds(301)));
    }

    [Fact]
    public void El_nack_de_una_entrega_anterior_se_rechaza()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);
        var segundoReclamo = Ahora + TimeSpan.FromSeconds(300);
        evento.VencerLease(segundoReclamo);
        evento.Reclamar(segundoReclamo);

        Assert.Throws<TransicionInvalidaException>(() =>
            evento.Nack("motivo", 1, segundoReclamo + TimeSpan.FromMinutes(1)));

        evento.Nack("motivo", 2, segundoReclamo + TimeSpan.FromMinutes(1));
        Assert.Equal(EstadoEvento.Fallido, evento.Estado);
    }

    [Fact]
    public void Disponer_antes_de_la_espera_es_prohibido()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);
        evento.Nack("motivo", 1, Ahora + TimeSpan.FromMinutes(1));

        Assert.Throws<TransicionInvalidaException>(() =>
            evento.Disponer(Ahora + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Disponer_devuelve_a_pendiente_cuando_quedan_entregas()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);
        evento.Nack("motivo", 1, Ahora + TimeSpan.FromMinutes(1));

        evento.Disponer(evento.DisponibleEn!.Value);

        Assert.Equal(EstadoEvento.Pendiente, evento.Estado);
        Assert.Null(evento.DisponibleEn);
    }

    [Fact]
    public void El_quinto_nack_lleva_a_muertos_al_disponer()
    {
        var evento = Nuevo();
        var momento = Ahora;

        for (var entrega = 1; entrega <= Evento.MaximoEntregas; entrega++)
        {
            Assert.Equal(ResultadoReclamo.Entregado, evento.Reclamar(momento));
            evento.Nack("fallo", entrega, momento + TimeSpan.FromMinutes(1));
            momento = evento.DisponibleEn!.Value;
            evento.Disponer(momento);
        }

        Assert.Equal(EstadoEvento.Muerto, evento.Estado);
        Assert.Equal(Evento.MaximoEntregas, evento.Intentos);
        Assert.Equal("fallo", evento.Resultado?.Motivo);
    }

    [Fact]
    public void El_sexto_reclamo_aparta_un_evento_sin_ack()
    {
        var evento = Nuevo();
        var momento = Ahora;

        for (var entrega = 1; entrega <= Evento.MaximoEntregas; entrega++)
        {
            evento.Reclamar(momento);
            momento += TimeSpan.FromSeconds(300);
            evento.VencerLease(momento);
        }

        var resultado = evento.Reclamar(momento);

        Assert.Equal(ResultadoReclamo.Apartado, resultado);
        Assert.Equal(EstadoEvento.Muerto, evento.Estado);
        Assert.Null(evento.LeaseHasta);
        Assert.Equal("cinco entregas sin ack", evento.Resultado?.Motivo);
    }

    [Fact]
    public void Vencer_lease_antes_de_tiempo_es_prohibido()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);

        Assert.Throws<TransicionInvalidaException>(() =>
            evento.VencerLease(Ahora + TimeSpan.FromSeconds(299)));
    }

    [Fact]
    public void Vencer_lease_devuelve_a_pendiente_y_se_puede_reclamar()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);
        var vencido = Ahora + TimeSpan.FromSeconds(300);

        evento.VencerLease(vencido);

        Assert.Equal(EstadoEvento.Pendiente, evento.Estado);
        Assert.Null(evento.LeaseHasta);

        Assert.Equal(ResultadoReclamo.Entregado, evento.Reclamar(vencido));
        Assert.Equal(2, evento.Intentos);
        Assert.Equal(vencido + TimeSpan.FromSeconds(300), evento.LeaseHasta);
    }

    [Fact]
    public void Aplicado_no_admite_mas_transiciones()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);
        evento.Ack("hash", 1, Ahora + TimeSpan.FromMinutes(1));

        Assert.Throws<TransicionInvalidaException>(() => evento.Reclamar(Ahora + TimeSpan.FromMinutes(2)));
        Assert.Throws<TransicionInvalidaException>(() =>
            evento.Ack("hash", 1, Ahora + TimeSpan.FromMinutes(2)));
        Assert.Throws<TransicionInvalidaException>(() =>
            evento.Nack("motivo", 1, Ahora + TimeSpan.FromMinutes(2)));
        Assert.Throws<TransicionInvalidaException>(() => evento.VencerLease(Ahora + TimeSpan.FromMinutes(2)));
        Assert.Throws<TransicionInvalidaException>(() => evento.Disponer(Ahora + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Muerto_no_admite_mas_transiciones()
    {
        var evento = Nuevo();
        var momento = Ahora;

        for (var entrega = 1; entrega <= Evento.MaximoEntregas; entrega++)
        {
            evento.Reclamar(momento);
            evento.Nack("fallo", entrega, momento + TimeSpan.FromMinutes(1));
            momento = evento.DisponibleEn!.Value;
            evento.Disponer(momento);
        }

        Assert.Throws<TransicionInvalidaException>(() => evento.Reclamar(momento));
        Assert.Throws<TransicionInvalidaException>(() => evento.Ack("hash", 5, momento));
        Assert.Throws<TransicionInvalidaException>(() => evento.Nack("motivo", 5, momento));
        Assert.Throws<TransicionInvalidaException>(() => evento.VencerLease(momento));
        Assert.Throws<TransicionInvalidaException>(() => evento.Disponer(momento));
    }

    [Fact]
    public void La_espera_crece_y_se_capa()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), Evento.EsperaReintento(1));
        Assert.Equal(TimeSpan.FromSeconds(120), Evento.EsperaReintento(2));
        Assert.Equal(TimeSpan.FromSeconds(240), Evento.EsperaReintento(3));
        Assert.Equal(TimeSpan.FromSeconds(300), Evento.EsperaReintento(4));
        Assert.Equal(TimeSpan.FromSeconds(300), Evento.EsperaReintento(5));
    }

    [Fact]
    public void Actualizar_reemplaza_tipo_y_carga_mientras_esta_pendiente()
    {
        var evento = Nuevo();
        using var carga = JsonDocument.Parse("""{"sesion":"ses_02"}""");

        evento.Actualizar("cierre.sesion.v2", carga.RootElement);

        Assert.Equal("cierre.sesion.v2", evento.Tipo);
        Assert.Equal("ses_02", evento.Carga.GetProperty("sesion").GetString());
        Assert.Equal(EstadoEvento.Pendiente, evento.Estado);
    }

    [Fact]
    public void Actualizar_en_fallido_conserva_intentos_espera_y_resultado()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);
        evento.Nack("motivo", 1, Ahora + TimeSpan.FromMinutes(1));
        var espera = evento.DisponibleEn;
        using var carga = JsonDocument.Parse("""{"sesion":"ses_02"}""");

        evento.Actualizar(Tipo, carga.RootElement);

        Assert.Equal(EstadoEvento.Fallido, evento.Estado);
        Assert.Equal(1, evento.Intentos);
        Assert.Equal(espera, evento.DisponibleEn);
        Assert.Equal(Ahora, evento.PublicadoEn);
        Assert.Equal("motivo", evento.Resultado?.Motivo);
    }

    [Fact]
    public void Actualizar_tras_perder_el_lease_es_prohibido()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);
        evento.VencerLease(Ahora + TimeSpan.FromSeconds(300));
        using var carga = JsonDocument.Parse("""{"sesion":"ses_02"}""");

        Assert.Throws<TransicionInvalidaException>(() => evento.Actualizar(Tipo, carga.RootElement));
        Assert.Equal("ses_01", evento.Carga.GetProperty("sesion").GetString());
    }

    [Fact]
    public void Actualizar_en_muerto_es_prohibido()
    {
        var evento = Nuevo();
        var momento = Ahora;

        for (var entrega = 1; entrega <= Evento.MaximoEntregas; entrega++)
        {
            evento.Reclamar(momento);
            evento.Nack("fallo", entrega, momento + TimeSpan.FromMinutes(1));
            momento = evento.DisponibleEn!.Value;
            evento.Disponer(momento);
        }

        using var carga = JsonDocument.Parse("{}");

        Assert.Throws<TransicionInvalidaException>(() => evento.Actualizar(Tipo, carga.RootElement));
    }

    [Fact]
    public void Actualizar_en_reclamado_es_prohibido()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);
        using var carga = JsonDocument.Parse("{}");

        Assert.Throws<TransicionInvalidaException>(() => evento.Actualizar(Tipo, carga.RootElement));
    }

    [Fact]
    public void Actualizar_en_aplicado_es_prohibido()
    {
        var evento = Nuevo();
        evento.Reclamar(Ahora);
        evento.Ack("hash", 1, Ahora + TimeSpan.FromMinutes(1));
        using var carga = JsonDocument.Parse("{}");

        Assert.Throws<TransicionInvalidaException>(() => evento.Actualizar(Tipo, carga.RootElement));
    }

    [Fact]
    public void Actualizar_con_carga_invalida_no_cambia_el_evento()
    {
        var evento = Nuevo();
        using var carga = JsonDocument.Parse("[]");

        Assert.Throws<ArgumentException>(() => evento.Actualizar(Tipo, carga.RootElement));
        Assert.Equal("ses_01", evento.Carga.GetProperty("sesion").GetString());
    }

    [Fact]
    public void El_identificador_debe_ser_un_ulid_con_prefijo()
    {
        using var carga = JsonDocument.Parse("{}");

        Assert.Throws<ArgumentException>(() =>
            Evento.Nuevo("01ARZ3NDEKTSV4RRFFQ69G5FAV", Tipo, carga.RootElement, Ahora));
        Assert.Throws<ArgumentException>(() =>
            Evento.Nuevo("evt_01arz3ndektsv4rrffq69g5fav", Tipo, carga.RootElement, Ahora));
    }

    [Fact]
    public void El_tipo_no_admite_espacios_ni_mayusculas()
    {
        using var carga = JsonDocument.Parse("{}");

        Assert.Throws<ArgumentException>(() =>
            Evento.Nuevo(Id, "Cierre sesión", carga.RootElement, Ahora));
    }

    [Fact]
    public void La_carga_debe_ser_un_objeto()
    {
        using var carga = JsonDocument.Parse("[]");

        Assert.Throws<ArgumentException>(() =>
            Evento.Nuevo(Id, Tipo, carga.RootElement, Ahora));
    }

    [Fact]
    public void La_carga_de_mas_de_64_kib_es_rechazada()
    {
        var texto = new string('a', 70 * 1024);
        using var carga = JsonDocument.Parse($$"""{"texto":"{{texto}}"}""");

        Assert.Throws<ArgumentException>(() =>
            Evento.Nuevo(Id, Tipo, carga.RootElement, Ahora));
    }

    [Fact]
    public void El_esquema_versionado_declara_los_campos_y_los_estados_del_dominio()
    {
        using var esquema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RaizDelRepositorio(), "contracts", "evento.v1.schema.json")));
        var raiz = esquema.RootElement;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", raiz.GetProperty("$schema").GetString());

        var requeridos = raiz.GetProperty("required")
            .EnumerateArray()
            .Select(campo => campo.GetString())
            .ToHashSet();
        foreach (var campo in new[]
                 {
                     "id", "tipo", "carga", "publicado_en", "estado", "intentos", "lease_hasta", "resultado"
                 })
        {
            Assert.Contains(campo, requeridos);
        }

        var estados = raiz.GetProperty("properties").GetProperty("estado").GetProperty("enum")
            .EnumerateArray()
            .Select(estado => estado.GetString());
        Assert.Equal(Enum.GetNames<EstadoEvento>().Select(estado => estado.ToLowerInvariant()), estados);
        Assert.Equal(
            Evento.MaximoEntregas,
            raiz.GetProperty("properties").GetProperty("intentos").GetProperty("maximum").GetInt32());
    }

    [Fact]
    public void El_contrato_openapi_versionado_declara_rutas_esquemas_y_entrega()
    {
        var raizDelRepositorio = RaizDelRepositorio();
        using var openapi = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(raizDelRepositorio, "contracts", "eventos.v1.openapi.json")));
        var raiz = openapi.RootElement;

        Assert.Equal("3.1.0", raiz.GetProperty("openapi").GetString());
        Assert.Equal("1.0.0", raiz.GetProperty("info").GetProperty("version").GetString());

        var rutas = raiz.GetProperty("paths");
        foreach (var ruta in new[]
                 {
                     "/v1/eventos", "/v1/eventos/siguiente", "/v1/eventos/{id}", "/v1/eventos/{id}/ack",
                     "/v1/eventos/{id}/nack", "/health"
                 })
        {
            Assert.True(rutas.TryGetProperty(ruta, out _), $"Falta la ruta {ruta}.");
        }

        var permisos = new (string Ruta, string Metodo, string Permiso)[]
        {
            ("/v1/eventos", "post", "eventos.publicar"),
            ("/v1/eventos", "get", "eventos.leer"),
            ("/v1/eventos/siguiente", "post", "eventos.consumir"),
            ("/v1/eventos/{id}", "get", "eventos.leer"),
            ("/v1/eventos/{id}/ack", "post", "eventos.consumir"),
            ("/v1/eventos/{id}/nack", "post", "eventos.consumir")
        };
        foreach (var (ruta, metodo, permiso) in permisos)
        {
            Assert.Equal(
                permiso,
                rutas.GetProperty(ruta).GetProperty(metodo).GetProperty("x-permiso").GetString());
        }

        Assert.True(rutas.GetProperty("/health").TryGetProperty("get", out _), "Falta la operación de salud.");

        var esquemas = raiz.GetProperty("components").GetProperty("schemas");
        var estados = esquemas.GetProperty("EstadoEvento").GetProperty("enum")
            .EnumerateArray()
            .Select(estado => estado.GetString());
        Assert.Equal(Enum.GetNames<EstadoEvento>().Select(estado => estado.ToLowerInvariant()), estados);

        using var esquemaSobre = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(raizDelRepositorio, "contracts", "evento.v1.schema.json")));
        Assert.Equal(
            esquemaSobre.RootElement.GetProperty("properties").GetProperty("tipo").GetProperty("pattern").GetString(),
            esquemas.GetProperty("Evento").GetProperty("properties").GetProperty("tipo").GetProperty("pattern")
                .GetString());

        foreach (var solicitud in new[] { "SolicitudAck", "SolicitudNack" })
        {
            var requeridos = esquemas.GetProperty(solicitud).GetProperty("required")
                .EnumerateArray()
                .Select(campo => campo.GetString())
                .ToHashSet();
            Assert.Contains("intentos", requeridos);
        }
    }

    private static Evento Nuevo()
    {
        using var carga = JsonDocument.Parse("""{"sesion":"ses_01"}""");
        return Evento.Nuevo(Id, Tipo, carga.RootElement, Ahora);
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "EventosApi.slnx")))
        {
            directorio = directorio.Parent;
        }

        Assert.NotNull(directorio);
        return directorio!.FullName;
    }
}
