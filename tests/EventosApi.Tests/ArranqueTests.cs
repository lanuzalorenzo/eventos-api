using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventosApi.Tests;

/// <summary>El host no arranca sin emisor ni audiencia: no hay modo anónimo.</summary>
public sealed class ArranqueTests
{
    [Fact]
    public void Sin_emisor_ni_audiencia_el_arranque_falla()
    {
        using var fabrica = new FabricaSinAutenticacion();

        var error = Assert.Throws<InvalidOperationException>(() => fabrica.CreateClient());

        Assert.Contains("modo anónimo", error.Message, StringComparison.Ordinal);
    }

    private sealed class FabricaSinAutenticacion : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Autenticacion:Emisor", string.Empty);
            builder.UseSetting("Autenticacion:Audiencia", string.Empty);
            builder.UseSetting(
                "ConnectionStrings:Cola",
                "Host=127.0.0.1;Port=1;Database=eventos;Username=eventos;Password=eventos-local");
            builder.UseSetting("Database:MigrarAlArrancar", "false");
        }
    }
}
