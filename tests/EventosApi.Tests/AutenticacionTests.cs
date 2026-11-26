using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EventosApi.Tests;

/// <summary>
/// Contrato de acceso: sin token, 401; con token válido y sin el permiso de la
/// ruta, 403. La sonda /health sigue siendo anónima.
/// </summary>
public sealed class AutenticacionTests(ApiFactory fabrica) : IClassFixture<ApiFactory>
{
    private const string Id = "evt_01ARZ3NDEKTSV4RRFFQ69G5FAV";

    public static TheoryData<string, string, string> Rutas => new()
    {
        { "POST", "/v1/eventos", "eventos.publicar" },
        { "GET", "/v1/eventos", "eventos.leer" },
        { "POST", "/v1/eventos/siguiente?tipo=cierre.sesion", "eventos.consumir" },
        { "GET", $"/v1/eventos/{Id}", "eventos.leer" },
        { "POST", $"/v1/eventos/{Id}/ack", "eventos.consumir" },
        { "POST", $"/v1/eventos/{Id}/nack", "eventos.consumir" },
    };

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Sin_token_responde_401_con_problem_details(
        string metodo,
        string ruta,
        string _)
    {
        using var cliente = fabrica.CreateClient();

        var respuesta = await EnviarAsync(cliente, metodo, ruta);

        await AssertProblemaAsync(respuesta, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Sin_el_permiso_de_la_ruta_responde_403_con_problem_details(
        string metodo,
        string ruta,
        string permiso)
    {
        var otros = ClienteDePrueba.TodosLosPermisos.Where(p => p != permiso).ToArray();
        using var cliente = fabrica.ConPermisos(otros);

        var respuesta = await EnviarAsync(cliente, metodo, ruta);

        await AssertProblemaAsync(respuesta, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Un_token_de_otro_emisor_responde_401()
    {
        using var cliente = fabrica.ConToken(
            fabrica.EmisorDePrueba().CreateToken(issuer: "https://otro.test/"));

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await cliente.GetAsync("/v1/eventos")).StatusCode);
    }

    [Fact]
    public async Task Un_token_de_otra_audiencia_responde_401()
    {
        using var cliente = fabrica.ConToken(
            fabrica.EmisorDePrueba().CreateToken(audience: "otra-api"));

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await cliente.GetAsync("/v1/eventos")).StatusCode);
    }

    [Fact]
    public async Task Un_token_vencido_responde_401()
    {
        using var cliente = fabrica.ConToken(
            fabrica.EmisorDePrueba().CreateToken(lifetime: TimeSpan.FromMinutes(-5)));

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await cliente.GetAsync("/v1/eventos")).StatusCode);
    }

    [Fact]
    public async Task Un_token_firmado_con_otra_clave_responde_401()
    {
        var otroEmisor = new LL.Security.LocalJwtIssuer("eventos-api");
        using var cliente = fabrica.ConToken(otroEmisor.CreateToken());

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await cliente.GetAsync("/v1/eventos")).StatusCode);
    }

    [Fact]
    public async Task Health_sigue_siendo_anonimo()
    {
        using var cliente = fabrica.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/health")).StatusCode);
    }

    private static async Task AssertProblemaAsync(
        HttpResponseMessage respuesta,
        HttpStatusCode estado)
    {
        Assert.Equal(estado, respuesta.StatusCode);
        Assert.Equal("application/problem+json", respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)estado, problema.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problema.GetProperty("title").GetString()));
    }

    private static Task<HttpResponseMessage> EnviarAsync(
        HttpClient cliente,
        string metodo,
        string ruta)
    {
        if (metodo == "GET")
        {
            return cliente.GetAsync(ruta);
        }

        object? cuerpo = ruta switch
        {
            var r when r.EndsWith("/ack", StringComparison.Ordinal) =>
                new { hash = "0123456789abcdef", intentos = 1 },
            var r when r.EndsWith("/nack", StringComparison.Ordinal) =>
                new { motivo = "prueba", intentos = 1 },
            var r when r.Contains("/siguiente", StringComparison.Ordinal) => null,
            _ => new { id = Id, tipo = "cierre.sesion", carga = new { } },
        };

        return cuerpo is null
            ? cliente.PostAsync(ruta, content: null)
            : cliente.PostAsJsonAsync(ruta, cuerpo);
    }
}
