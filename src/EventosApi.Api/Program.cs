using System.Security.Claims;
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

// Cada permiso del contrato es una política. Entra ID entrega los permisos
// como roles de aplicación en `roles`; el mapeo de claims lo convierte en
// ClaimTypes.Role, que es el claim que exige la política.
builder.Services.AddPermissionPolicies(opciones => opciones.ClaimType = ClaimTypes.Role);

if (builder.Environment.IsEnvironment("Testing"))
{
    // Pruebas de contrato: el mismo JwtBearer, sin autoridad remota y con una
    // clave simétrica que solo vive en el proceso.
    var emisorLocal = new LocalJwtIssuer("eventos-api");
    builder.Services.AddSingleton(emisorLocal);
    builder.Services.AddLocalJwtBearer(emisorLocal);
}
else
{
    // Sin emisor ni audiencia válidos el arranque falla: no hay modo anónimo.
    var emisor = builder.Configuration["Autenticacion:Emisor"];
    var audiencia = builder.Configuration["Autenticacion:Audiencia"];
    if (string.IsNullOrWhiteSpace(emisor) || string.IsNullOrWhiteSpace(audiencia))
    {
        throw new InvalidOperationException(
            "Falta Autenticacion:Emisor o Autenticacion:Audiencia. No hay modo anónimo.");
    }

    builder.Services.AddHardenedJwtBearer(opciones =>
    {
        opciones.Authority = emisor;
        opciones.Audience = audiencia;
    });
}

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
// Las respuestas 401 y 403 de la autenticación no traen cuerpo: el contrato
// promete problem+json y esta página lo escribe sin tocar el resto de estados.
app.UseStatusCodePages(async contexto =>
{
    var respuesta = contexto.HttpContext.Response;
    if (respuesta.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
    {
        await Results.Problem(
            statusCode: respuesta.StatusCode,
            title: respuesta.StatusCode == StatusCodes.Status401Unauthorized
                ? "No autenticado"
                : "Sin permiso")
            .ExecuteAsync(contexto.HttpContext);
    }
});
app.UseApiTransportSecurity(app.Environment);
app.UseCors("ApiCors");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapEventos();
// La sonda de la plataforma no comparte el cupo del tráfico de la API.
app.MapApiHealthChecks().DisableRateLimiting();

app.Run();

// Expuesto para que las pruebas de integración usen WebApplicationFactory<Program>.
public partial class Program;
