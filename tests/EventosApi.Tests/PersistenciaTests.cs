using System.Text.Json;
using EventosApi.Domain;
using EventosApi.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EventosApi.Tests;

public sealed class PersistenciaTests
{
    private static ColaDbContext Contexto() => new(
        new DbContextOptionsBuilder<ColaDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=eventos;Username=eventos;Password=eventos-local")
            .Options);

    [Fact]
    public void El_estado_se_guarda_en_minusculas()
    {
        using var db = Contexto();
        var convertidor = Convertidor(db, nameof(Evento.Estado));

        Assert.Equal("pendiente", convertidor.ConvertToProvider(EstadoEvento.Pendiente));
        Assert.Equal("reclamado", convertidor.ConvertToProvider(EstadoEvento.Reclamado));
        Assert.Equal(EstadoEvento.Muerto, convertidor.ConvertFromProvider("muerto"));
    }

    [Fact]
    public void El_resultado_va_y_vuelve_como_json()
    {
        using var db = Contexto();
        var convertidor = Convertidor(db, nameof(Evento.Resultado));

        var json = (string?)convertidor.ConvertToProvider(new ResultadoEvento(null, "timeout"));
        Assert.Equal("""{"motivo":"timeout"}""", json);

        var vuelta = (ResultadoEvento?)convertidor.ConvertFromProvider(json);
        Assert.Equal("timeout", vuelta?.Motivo);
        Assert.Null(convertidor.ConvertToProvider(null));
    }

    [Fact]
    public void La_carga_se_guarda_y_vuelve_como_json()
    {
        using var db = Contexto();
        var convertidor = Convertidor(db, nameof(Evento.Carga));
        using var documento = JsonDocument.Parse("""{"sesion":"ses_01"}""");

        var json = (string?)convertidor.ConvertToProvider(documento.RootElement);
        Assert.Equal("""{"sesion":"ses_01"}""", json);

        var vuelta = (JsonElement)convertidor.ConvertFromProvider(json)!;
        Assert.Equal("ses_01", vuelta.GetProperty("sesion").GetString());
    }

    private static ValueConverter Convertidor(ColaDbContext db, string propiedad)
    {
        var convertidor = db.Model
            .FindEntityType(typeof(Evento))!
            .FindProperty(propiedad)!
            .GetValueConverter();

        Assert.NotNull(convertidor);
        return convertidor;
    }
}
