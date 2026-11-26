# Guía para agentes: EventosApi

Servicio de eventos del cierre en .NET 10. Guarda un sobre opaco que produce otro proceso y lo entrega con lease; no conoce el dominio del consumidor. Este archivo cubre el contrato, la estructura, la seguridad y la verificación del repositorio.

## Contrato

- La fuente es `contracts/eventos.v1.openapi.json` para HTTP y `contracts/evento.v1.schema.json` para el sobre; el dominio y las pruebas no pueden contradecirlos. Un cambio de contrato empieza en esos ficheros, con su ADR si es estructural, y baja después al código.
- La versión va en la ruta (`/v1`). No cambies el significado de un campo de v1: una ruptura abre v2.
- La carga es opaca y su esquema pertenece al consumidor. No metas en el servicio reglas del dominio del cierre ni de ningún otro productor.
- Estados: `pendiente`, `reclamado`, `aplicado`, `fallido`, `muerto`; solo cambian por las transiciones del contrato. El lease dura 300 s, hay cinco entregas como máximo y el ack solo vale para la entrega vigente.
- La idempotencia del alta y el bloqueo de fila del reclamo viven en PostgreSQL; el fencing por entrega, en el dominio. No los muevas a memoria ni los sustituyas por un cerrojo global.

## Arquitectura

- Desacoplada: `Domain` sin dependencias, `Application` solo `Domain`, `Infrastructure` de ambas y el host compone todo. No debilites esa separación.
- Toda decisión estructural se documenta como ADR en `docs/adr/`, con su motivo y sus alternativas.
- `Directory.Build.props` fija la configuración común de compilación y `Directory.Packages.props` la única versión de cada paquete; los `.csproj` no repiten `Nullable`, `ImplicitUsings` ni `Version`.

## Seguridad

- Security by Design y OWASP API Security Top 10.
- Los controles compartidos se consumen desde `LL.Security` y `LL.Hosting`; no los reimplementes en el host.
- Autenticación: `AddHardenedJwtBearer` con emisor y audiencia obligatorios fuera de Testing; sin valores, el arranque falla. Las pruebas usan `AddLocalJwtBearer` con `LocalJwtIssuer`, nunca un modo anónimo. No añadas ninguna bandera que desactive la autenticación.
- Permisos: `AddPermissionPolicies` con `ClaimType = ClaimTypes.Role` (los roles de aplicación de Entra ID) y `RequirePermission` por operación, según la tabla del contrato (`eventos.publicar`, `eventos.consumir`, `eventos.leer`). El emisor local de pruebas emite el mismo claim. `UseAuthentication`/`UseAuthorization` va antes de los endpoints.
- Las respuestas 401 y 403 llevan `application/problem+json`; si tocas el pipeline, conserva esa página de estado. Fija los títulos del contrato v1, por eso vive en el host: si otro consumidor necesita la misma página, sube a `LL.Hosting` con su prueba.
- CORS con denegación por defecto y orígenes explícitos.
- Dockerfile con usuario no privilegiado (`USER $APP_UID`). El CI construye la imagen, comprueba que no ejecuta como root y la escanea con Trivy; las dependencias se escanean con el mismo Trivy sobre los manifiestos.
- Sin secretos ni cadenas reales en el código: `appsettings.Local.json` (ignorado), user-secrets o variables de entorno. `appsettings.Development.json` se versiona sin secretos.
- La sonda `/health` es anónima y queda fuera del limitador; el resto de la API no se abre sin pasar por los controles del host.
- Pruebas de integración en `tests/EventosApi.Tests` para cualquier cambio de comportamiento.

## Verificación

```bash
dotnet restore EventosApi.slnx
dotnet build EventosApi.slnx -c Release --no-restore
dotnet test EventosApi.slnx -c Release --no-build --no-restore
dotnet format EventosApi.slnx --verify-no-changes --no-restore
gitleaks detect --source . --redact --verbose --exit-code 2
```

Las pruebas de integración necesitan Docker: `PostgresFixture` levanta PostgreSQL 18 en un contenedor y sin él `dotnet test` falla. Las pruebas no ven la cabecera `Server` porque `TestServer` no la escribe. Para comprobarla, arranca la API y mira `/health`:

```bash
ASPNETCORE_URLS=http://127.0.0.1:8099 dotnet run --project src/EventosApi.Api --no-launch-profile &
curl -sI http://127.0.0.1:8099/health | grep -i '^server:' && echo "publica Server" || echo "sin cabecera Server"
```

El CI usa las acciones de `core` para compilar, escanear secretos, analizar el código y escanear dependencias e imagen. El job de SCA restaura y compila antes de `scan`, como pide `core`; la construcción del contenedor, la comprobación de usuario no privilegiado y la revisión de dependencias van en el mismo workflow. **Definición de cambio listo:** los cinco comandos anteriores sin fallos y el pipeline en verde, sin bypass.

## Dependabot

Si una PR de Dependabot queda `BEHIND`, actualiza su rama con `gh api -X PUT repos/OWNER/REPO/pulls/N/update-branch`; el push resultante dispara CI y el auto-merge la fusiona. No se automatiza con `GITHUB_TOKEN`: sus eventos no disparan workflows y Dependabot rechaza los comandos de `github-actions[bot]`.

## Commits

Formato: `type(scope): imperative summary in English`. Scopes: `api`, `ci`, `tests`, `docs`, `repo`. Tipos: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`.

Los cambios entran por rama y pull request: `main` exige PR y los checks del pipeline.
