# ADR-0003 — Publicación idempotente y reclamo con bloqueo de fila

> **Estado:** Aceptada · 2026-11-24
> **Ámbito:** `src/EventosApi.Application/Eventos`, `src/EventosApi.Infrastructure/Persistencia/ColaEventos.cs`, `src/EventosApi.Api/Endpoints` y las pruebas de integración contra PostgreSQL.
> **Evidencia:** 53 pruebas en verde en local, 11 de ellas de integración contra PostgreSQL 18 en contenedor: dos publicaciones del mismo id dejan un registro, dos reclamos simultáneos entregan una vez, el reclamo salta la fila bloqueada por otra sesión y el lease vencido o la espera del fallido devuelven el evento a la cola. El pipeline de la PR queda como comprobación de cierre.
> **Relacionada con:** [ADR-0001](0001-contrato-del-evento.md), que fija el sobre y los estados, y [ADR-0002](0002-persistencia-postgres-esquema-cola.md), que fija la tabla y sus restricciones.

## 1. Contexto

El contrato v1 define `POST /v1/eventos` como idempotente por `id` y `POST /v1/eventos/siguiente` como una entrega única con lease de 300 s. La tabla existe desde ADR-0002, pero nada impide que dos sesiones publiquen el mismo cierre dos veces ni que dos consumidores reclamen el mismo evento a la vez.

La idempotencia y la exclusión no pueden vivir solo en el código: dos procesos concurrentes leen y escriben a la vez. Hace falta que PostgreSQL decida quién gana, sin un cerrojo global que serialice toda la cola.

## 2. Decisión

1. **Alta idempotente en una transacción con bloqueo de fila.** Se lee el evento por `id` con `SELECT … FOR UPDATE`. Si no existe, se crea con estado `pendiente` y se responde 201. Si existe y está `pendiente` o `fallido` y no perdió una entrega, se reemplazan `tipo` y `carga` y se responde 200; en cualquier otro estado, el dominio lanza la transición inválida y se responde 409. `publicado_en`, `intentos`, la espera y el resultado se conservan.
2. **La carrera del alta se resuelve con la clave única.** Si otra sesión inserta el mismo `id` entre la lectura y el alta, PostgreSQL devuelve `23505`; la persistencia lo traduce a `ConflictoDeUnicidadException` y el caso de uso reintenta una vez, ya como actualización. Dos publicaciones simultáneas acaban en un solo registro.
3. **Reclamo con `FOR UPDATE SKIP LOCKED`.** Se toma el candidato más antiguo del `tipo` pedido (`ORDER BY publicado_en, id LIMIT 1`): pendiente con la espera cumplida, fallido cuya espera de reintento ya venció, o reclamado con el lease vencido. Las filas que otra sesión está reclamando se saltan en vez de esperarlas; la segunda sesión recibe 204 si no queda nada. `SKIP LOCKED` evita serializar la cola entera detrás de una fila bloqueada.
4. **El lease vencido y el reintento vencido se resuelven en el propio reclamo.** Si el candidato está `reclamado` y su lease venció, se aplica `VencerLease` y se reclama de nuevo, con lo que suma un intento y queda marcada la entrega abandonada. Si está `fallido` y su espera ya terminó, se aplica `Disponer`; si agotó las cinco entregas pasa a muertos y el reclamo continúa con el siguiente candidato. No hay barrido en segundo plano: el estado se corrige cuando alguien vuelve a pedir trabajo.
5. **La transacción es del caso de uso, no del endpoint.** `IColaEventos.EnTransaccionAsync` agrupa lectura con bloqueo, decisión de dominio y guardado; la implementación es de Infrastructure y la orquestación, de Application.
6. **Nombres del contrato en JSON.** La API serializa y deserializa en `snake_case` y rechaza propiedades no declaradas, como fija el contrato v1.
7. **Pruebas contra PostgreSQL real.** Las pruebas de integración levantan PostgreSQL 18 en un contenedor con Testcontainers y aplican la migración. Los proveedores en memoria o SQLite no tienen `FOR UPDATE SKIP LOCKED` ni la misma semántica de clave única.

## 3. Alternativas descartadas

- **`INSERT … ON CONFLICT DO UPDATE`.** Deja la condición de estado y la bandera de entrega abandonada duplicadas en SQL, donde el dominio ya las decide; el mensaje de error y el 201/200 también se reparten entre capas.
- **Aislamiento serializable.** PostgreSQL abortaría transacciones concurrentes y obligaría a reintentos con más superficie de error; el bloqueo de fila basta y no serializa la cola.
- **Un cerrojo de aplicación o un `lock` en memoria.** No vale con varias réplicas y desaparece al reiniciar.
- **Barrido periódico de leases vencidos.** Añade un planificador para un estado que solo importa cuando alguien pide trabajo; el barrido perezoso en el reclamo cubre el contrato con menos piezas.
- **Pruebas con SQLite o el proveedor en memoria.** No reproducen el bloqueo ni la restricción de clave única que son el objeto de la prueba.

## 4. Consecuencias

- Dos reclamos simultáneos entregan una vez; el segundo recibe 204 si no hay otro candidato.
- Una publicación simultánea reintenta una vez como actualización; el reintento no deja entidades añadidas en el contexto.
- Un evento con lease vencido o fallido con la espera cumplida sigue en su estado hasta el siguiente reclamo; el listado y el barrido explícito quedan para los ítems que los piden.
- Las pruebas de integración necesitan Docker; el contenedor fija `postgres:18-alpine`, la misma imagen que `compose.yaml`.
- La transacción y el bloqueo son de PostgreSQL; si cambia el motor, esta decisión se revisa antes de portar el código.
