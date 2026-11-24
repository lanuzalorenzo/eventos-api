<img src="assets/iconos/eventos-api.svg" width="64" alt="Icono de Eventos API">

# EventosApi

API empresarial en .NET 10 con la línea base del portfolio: arquitectura por capas, compilación centralizada, controles compartidos, CI y contenedor sin privilegios.

## Estructura

- `src/EventosApi.Domain`: entidades e invariantes. Sin dependencias.
- `src/EventosApi.Application`: casos de uso y puertos. Solo depende de `Domain`.
- `src/EventosApi.Infrastructure`: adaptadores. Depende de `Application` y `Domain`.
- `src/EventosApi.Api`: host mínimo; compone los servicios y expone los endpoints.
- `contracts/`: JSON Schema y OpenAPI versionados del sobre y de la superficie HTTP.
- `docs/adr/`: decisiones estructurales, con su ADR 0001.
- `tests/EventosApi.Tests`: pruebas de contrato, transiciones y salud.

La regla de dependencias es `Domain ← Application ← Infrastructure ← Api`. No la debilites.

## Puesta en marcha

```bash
# PAT de solo lectura (read:packages) para el feed privado; no uses el token de gh.
export GH_PACKAGES_TOKEN="<pat-de-solo-lectura>"
dotnet restore EventosApi.slnx
dotnet run --project src/EventosApi.Api
```

`/health` responde sin autenticación y `/api/saludo` sirve el endpoint de ejemplo. La documentación OpenAPI solo se publica en Development.

## Controles

Los paquetes `LL.Security` y `LL.Hosting` aportan CORS con denegación por defecto, cabeceras de seguridad con la `Server` de Kestrel apagada, límite por IP, ProblemDetails, HTTPS/HSTS y salud anónima. La configuración entra por `Program.cs` y `appsettings.json`; los orígenes permitidos van en `Cors:AllowedOrigins`.

## Verificación

```bash
dotnet restore EventosApi.slnx
dotnet build EventosApi.slnx -c Release --no-restore
dotnet test EventosApi.slnx -c Release --no-build --no-restore
dotnet format EventosApi.slnx --verify-no-changes --no-restore
gitleaks detect --source . --redact --verbose --exit-code 2
```

El CI usa las acciones de [`core`](https://github.com/lanuzalorenzo/core) para compilar, escanear secretos, analizar el código y escanear dependencias e imagen; la construcción del contenedor, la comprobación de usuario no privilegiado y la revisión de dependencias van en el mismo workflow. Restaura los paquetes de [`shared-dotnet`](https://github.com/lanuzalorenzo/shared-dotnet). La versión de `LL.*` está fijada en `Directory.Packages.props`; se sube a mano cuando `shared-dotnet` publica una versión nueva.

## Docker

```bash
docker build --secret id=github_token,env=GH_PACKAGES_TOKEN -t company-api .
docker run --rm -p 8080:8080 company-api
```

El contenedor ejecuta con usuario no privilegiado y escucha HTTP en el 8080. La redirección a HTTPS se aplica en cualquier entorno salvo `Testing` cuando hay un puerto HTTPS configurado; HSTS se añade fuera de Development. En el contenedor no hay puerto HTTPS: la terminación TLS corresponde al host o a la plataforma, así que la imagen no redirige por sí sola.

Licencia: MIT.
