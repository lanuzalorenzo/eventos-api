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

## Observabilidad

- `Telemetria` en `src/EventosApi.Application/Eventos` es la única fuente de `ActivitySource` y `Meter`: fuente `EventosApi`, una traza por operación y `evento.id` en las que operan sobre un evento, y contadores `eventos.publicados`, `eventos.reclamados`, `eventos.aplicados`, `eventos.fallidos` y `eventos.muertos`.
- Los logs de cambio de estado llevan el identificador y nunca la carga. `Microsoft.EntityFrameworkCore.Database.Command` queda en `Warning` para que el SQL no copie el `jsonb`; la prueba de redacción falla si la carga aparece en un log.
- `/health` sigue comprobando la base y es anónima.

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

## Línea base

Los identificadores y el criterio son los de [`shared-dotnet/docs/linea-base.md`](https://github.com/lanuzalorenzo/shared-dotnet/blob/main/docs/linea-base.md). Esta tabla es la declaración canónica del estado; la evidencia es localizable y no convierte un control montado en un control probado.

| Id | Estado | Evidencia |
|---|---|---|
| `auth` | Parcial | `AddHardenedJwtBearer` con emisor y audiencia obligatorios; `AutenticacionTests` cubre 401 sin token, con otro emisor, otra audiencia, otra clave y token vencido. Falta un registro real de Entra ID con los app roles: hoy el contrato se prueba con el emisor local. |
| `permisos` | Parcial | `AddPermissionPolicies` con `ClaimTypes.Role` y `RequirePermission` en las seis rutas `/v1`; 403 en `AutenticacionTests`. Falta el app role real en Entra ID. |
| `cors` | Parcial | `AddDenyByDefaultCors` con orígenes explícitos y `UseCors("ApiCors")`; sin prueba propia en este repositorio (la cubre `LL.Security.Tests`). |
| `cabeceras` | Parcial | `UseSecurityHeaders`; `HealthTests` comprueba `X-Content-Type-Options` y `X-Frame-Options`. Falta la salida fechada de `/health` sin cabecera `Server`; el comando manual está en §Verificación. |
| `csp` | Pendiente | Sin `SecurityHeadersOptions.Csp` en el host; la API no sirve HTML, pero el control sigue pendiente. |
| `limite` | Parcial | `AddRemoteIpRateLimiting` y `UseRateLimiter()`, con la sonda fuera del cupo; sin prueba de 429 en este repositorio. |
| `problemdetails` | Validado | `AddApiProblemDetails` y `UseApiProblemDetails`; 401 y 403 con `application/problem+json` en `AutenticacionTests`. |
| `salud` | Validado | `AddApiHealthChecks` y `MapApiHealthChecks`; `/health` anónimo en `HealthTests` y `unhealthy` cuando la base no responde. |
| `permisos-workflow` | Validado | `permissions` explícito por workflow y por job; sin `write-all`. Escritura solo donde la operación la exige: `security-events` en SAST, SCA y contenedor, `pull-requests` en la revisión de dependencias y `contents` + `pull-requests` en el auto-merge de Dependabot. |
| `secretos` | Validado | Gitleaks sobre el historial completo en cada push y PR (`.github/workflows/ci.yml`, job `secret-scanning`). |
| `sast` | Validado | Job `sast` con CodeQL y subida de SARIF; verde en el [run sobre `main` 45819366110](https://github.com/lanuzalorenzo/eventos-api/actions/runs/45819366110) (2026-11-26). |
| `sca` | Validado | Job `sca` con Trivy sobre los manifiestos, bloqueante con el umbral por defecto de `core/actions/scan` (`CRITICAL,HIGH`); la subida de SARIF está activa. |
| `contenedor` | Validado | Job `container-security`: construye la imagen, comprueba que el UID no es 0 y la escanea con Trivy. |
| `dependabot` | Validado | `.github/dependabot.yml` para NuGet, Docker y GitHub Actions, y `dependabot-auto-merge.yml` para parche y menor. |
| `enlaces` | Validado | Job `links` con `check-links@v1.8` (entrada `root`) sobre los 8 documentos; verde en el [run sobre `main` 45819366110](https://github.com/lanuzalorenzo/eventos-api/actions/runs/45819366110). |
| `main` | Validado | Ruleset [`main`](https://github.com/lanuzalorenzo/eventos-api/rules/28163092) con pull request, siete checks, rama actualizada, historial lineal, squash y sin bypass; run de `workflow_dispatch` sobre `main` en verde ([45819366110](https://github.com/lanuzalorenzo/eventos-api/actions/runs/45819366110), 2026-11-26). |

## Commits

Formato: `type(scope): imperative summary in English`. Scopes: `api`, `ci`, `tests`, `docs`, `repo`. Tipos: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`.

Los cambios entran por rama y pull request: `main` exige PR y siete checks del pipeline, sin bypass.
