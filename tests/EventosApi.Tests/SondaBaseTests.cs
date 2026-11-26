using EventosApi.Infrastructure.Persistencia;
using EventosApi.Infrastructure.Salud;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EventosApi.Tests;

/// <summary>La sonda real abre su ámbito y comprueba la conexión de verdad.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SondaBaseTests(PostgresFixture postgres)
{
    [Fact]
    public async Task La_sonda_real_ve_la_base_del_contenedor()
    {
        Assert.True(await RespondeAsync(postgres.CadenaConexion));
    }

    [Fact]
    public async Task La_sonda_real_no_ve_una_base_inexistente()
    {
        var cadena = new NpgsqlConnectionStringBuilder(postgres.CadenaConexion)
        {
            Database = "no_existe_observabilidad",
        };

        Assert.False(await RespondeAsync(cadena.ConnectionString));
    }

    private static async Task<bool> RespondeAsync(string cadena)
    {
        var servicios = new ServiceCollection();
        servicios.AddDbContext<ColaDbContext>(opciones => opciones.UseNpgsql(cadena));
        await using var proveedor = servicios.BuildServiceProvider();

        var sonda = new SondaBase(proveedor.GetRequiredService<IServiceScopeFactory>());
        return await sonda.RespondeAsync(CancellationToken.None);
    }
}
