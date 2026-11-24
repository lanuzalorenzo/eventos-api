using EventosApi.Infrastructure.Salud;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventosApi.Tests;

public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly bool baseResponde;

    public ApiFactory()
        : this(true)
    {
    }

    protected ApiFactory(bool baseResponde)
    {
        this.baseResponde = baseResponde;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // El entorno de pruebas se salta la redirección a HTTPS: los clientes de
        // prueba hablan HTTP y una redirección les rompería la petición.
        builder.UseEnvironment("Testing");
        builder.UseSetting(
            "ConnectionStrings:Cola",
            "Host=127.0.0.1;Port=1;Database=eventos;Username=eventos;Password=eventos-local");

        builder.ConfigureTestServices(servicios =>
        {
            servicios.RemoveAll<ISondaBase>();
            servicios.AddSingleton<ISondaBase>(new SondaDePrueba(baseResponde));
        });
    }

    private sealed class SondaDePrueba(bool responde) : ISondaBase
    {
        public Task<bool> RespondeAsync(CancellationToken cancelacion) => Task.FromResult(responde);
    }
}

public sealed class ApiFactorySinBase() : ApiFactory(false);
