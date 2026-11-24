using System.Net;
using EventosApi.Infrastructure.Salud;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EventosApi.Tests;

public sealed class BaseDatosHealthCheckTests
{
    private static readonly HealthCheckContext Contexto = new();

    [Fact]
    public async Task Una_base_que_responde_da_healthy()
    {
        var sonda = new BaseDatosHealthCheck(new SondaFalsa(true));

        var resultado = await sonda.CheckHealthAsync(Contexto);

        Assert.Equal(HealthStatus.Healthy, resultado.Status);
    }

    [Fact]
    public async Task Una_base_que_no_responde_da_unhealthy()
    {
        var sonda = new BaseDatosHealthCheck(new SondaFalsa(false));

        var resultado = await sonda.CheckHealthAsync(Contexto);

        Assert.Equal(HealthStatus.Unhealthy, resultado.Status);
    }

    [Fact]
    public async Task Una_sonda_que_falla_da_unhealthy()
    {
        var sonda = new BaseDatosHealthCheck(new SondaQueFalla());

        var resultado = await sonda.CheckHealthAsync(Contexto);

        Assert.Equal(HealthStatus.Unhealthy, resultado.Status);
    }

    private sealed class SondaFalsa(bool responde) : ISondaBase
    {
        public Task<bool> RespondeAsync(CancellationToken cancelacion) => Task.FromResult(responde);
    }

    private sealed class SondaQueFalla : ISondaBase
    {
        public Task<bool> RespondeAsync(CancellationToken cancelacion) =>
            throw new InvalidOperationException("sin conexión");
    }
}

public sealed class HealthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_responde_sin_autenticacion()
    {
        var cliente = factory.CreateClient();

        var respuesta = await cliente.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task Las_respuestas_llevan_cabeceras_de_seguridad()
    {
        var cliente = factory.CreateClient();

        var respuesta = await cliente.GetAsync("/api/saludo");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("nosniff", respuesta.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", respuesta.Headers.GetValues("X-Frame-Options").Single());
    }
}

public sealed class HealthSinBaseTests(ApiFactorySinBase factory) : IClassFixture<ApiFactorySinBase>
{
    [Fact]
    public async Task Health_falla_cuando_la_base_no_responde()
    {
        var cliente = factory.CreateClient();

        var respuesta = await cliente.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, respuesta.StatusCode);
    }
}
