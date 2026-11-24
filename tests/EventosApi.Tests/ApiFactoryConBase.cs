using EventosApi.Infrastructure.Salud;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventosApi.Tests;

/// <summary>Host de pruebas apuntando al PostgreSQL del contenedor.</summary>
public sealed class ApiFactoryConBase(string cadenaConexion) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Cola", cadenaConexion);
        // La migración la aplica el fixture una sola vez para todos los casos.
        builder.UseSetting("Database:MigrarAlArrancar", "false");

        builder.ConfigureTestServices(servicios =>
        {
            servicios.RemoveAll<ISondaBase>();
            servicios.AddSingleton<ISondaBase>(new SondaSiempre(true));
        });
    }

    private sealed class SondaSiempre(bool responde) : ISondaBase
    {
        public Task<bool> RespondeAsync(CancellationToken cancelacion) => Task.FromResult(responde);
    }
}
