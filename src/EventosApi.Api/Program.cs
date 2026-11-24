using System.Text.Json;
using System.Text.Json.Serialization;
using EventosApi.Api.Endpoints;
using EventosApi.Application.Eventos;
using EventosApi.Infrastructure.Persistencia;
using EventosApi.Infrastructure.Salud;
using LL.Hosting;
using LL.Security;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var cadenaCola = builder.Configuration.GetConnectionString("Cola")
    ?? throw new InvalidOperationException(
        "Falta ConnectionStrings:Cola. En compose la pone el servicio; fuera de ahí, la configuración local.");

builder.Services.AddDbContext<ColaDbContext>(opciones => opciones.UseNpgsql(cadenaCola));
builder.Services.AddScoped<IColaEventos, ColaEventos>();
builder.Services.AddScoped<ServicioEventos>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISondaBase, SondaBase>();
builder.Services.AddHealthChecks().AddCheck<BaseDatosHealthCheck>("base");

// Nombres del contrato v1 en las dos direcciones y sin propiedades que no
// estén declaradas.
builder.Services.ConfigureHttpJsonOptions(opciones =>
{
    opciones.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    opciones.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    opciones.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});

builder.Services.AddOpenApi();
builder.Services.AddApiProblemDetails();
builder.Services.AddApiHealthChecks();
builder.Services.AddDenyByDefaultCors(builder.Configuration);
builder.Services.AddRemoteIpRateLimiting();

var app = builder.Build();

// En compose la migración la aplica el propio servicio; en local se puede
// desactivar y usar `dotnet ef database update`.
if (builder.Configuration.GetValue<bool>("Database:MigrarAlArrancar"))
{
    using var ambito = app.Services.CreateScope();
    await ambito.ServiceProvider.GetRequiredService<ColaDbContext>().Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// El orden importa: las cabeceras se escriben cuando empieza la respuesta y el
// manejador de excepciones va antes que el resto del pipeline de la aplicación.
app.UseSecurityHeaders();
app.UseApiProblemDetails();
app.UseApiTransportSecurity(app.Environment);
app.UseCors("ApiCors");
app.UseRateLimiter();

app.MapEventos();
app.MapGet("/api/saludo", () => Results.Ok(new { mensaje = "Hola" }));
// La sonda de la plataforma no comparte el cupo del tráfico de la API.
app.MapApiHealthChecks().DisableRateLimiting();

app.Run();

// Expuesto para que las pruebas de integración usen WebApplicationFactory<Program>.
public partial class Program;
