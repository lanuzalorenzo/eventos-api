# ADR-0004 — Cierre de la entrega: ack, nack, consulta y listado

> **Estado:** Aceptada · 2026-11-25
> **Ámbito:** `src/EventosApi.Application/Eventos` (casos de uso y cursor), `src/EventosApi.Infrastructure/Persistencia/ColaEventos.cs`, `src/EventosApi.Api/Endpoints`, `contracts/eventos.v1.openapi.json` y las pruebas de integración contra PostgreSQL.
> **Evidencia:** 59 pruebas en verde en local, 17 de ellas de integración contra PostgreSQL 18 en contenedor. Cubren el recorrido publicar → reclamar → ack con hash → consulta, el ack con un número de entrega que no es el vigente (409), los cinco nacks con su motivo hasta muerto, la paginación por cursor sin repetir ni perder filas y con el desempate por identificador, el 404 de un identificador válido inexistente y el 400 de los parámetros fuera de contrato. El pipeline de la PR queda como comprobación de cierre.
> **Relacionada con:** [ADR-0001](0001-contrato-del-evento.md), que fija el sobre y las transiciones; [ADR-0002](0002-persistencia-postgres-esquema-cola.md), que fija la tabla; y [ADR-0003](0003-publicacion-idempotente-y-reclamo-con-bloqueo.md), que fija el alta y el reclamo.

## 1. Contexto

El contrato v1 ya declaraba `GET /v1/eventos`, `GET /v1/eventos/{id}`, `POST /v1/eventos/{id}/ack` y `POST /v1/eventos/{id}/nack`, pero el host solo exponía publicar y reclamar. El dominio tenía las transiciones desde ADR-0001; faltaba la puerta, la transacción y la prueba del ciclo completo.

El ack es la prueba de que el cambio se aplicó: lleva el hash del commit. Por eso no puede aceptarse un ack de una entrega anterior ni de un lease vencido: el evento pudo reclamarse otra vez y el mismo cierre se aplicaría dos veces.

## 2. Decisión

1. **Ack y nack en una transacción con bloqueo de fila.** Se lee el evento por `id` con `SELECT … FOR UPDATE`, se aplica la transición de dominio y se guarda. El endpoint no decide estados: traduce la excepción de transición a 409.
2. **El número de entrega es el testigo.** `intentos` viaja en el cuerpo y debe coincidir con el de la entrega vigente; si no, 409. El lease vencido también es 409. El `hash` del ack se guarda como `resultado.hash`; el `motivo` del nack, como `resultado.motivo`.
3. **Nack fija la espera; muerto se decide al disponer.** El nack deja el evento `fallido` con `30 s × 2^intentos`, tope 300 s. Pasada la espera, el siguiente reclamo lo devuelve a `pendiente` o, si ya hizo las cinco entregas, lo aparta en `muerto` conservando el motivo del último nack. No hay barrido en segundo plano: el estado se corrige cuando alguien pide trabajo, como en ADR-0003.
4. **Identificador válido e inexistente es 404; malformado es 400.** La ruta se valida con la misma regla del dominio (`evt_` + ULID en mayúsculas). Un identificador bien formado que no está en la cola no es un error de sintaxis.
5. **Lectura sin bloqueo.** Consulta y listado usan `AsNoTracking` y no abren transacción; no compiten con el reclamo ni con el ack.
6. **Listado por cursor de clave, no por offset.** La clave de orden es `(publicado_en, id)`; el cursor es esa clave en base64url, opaco para el cliente. El límite es 50 por defecto y 200 como máximo, y la consulta pide una fila de más para saber si hay página siguiente. Un offset se desplazaría con cada alta y repetiría o perdería filas; el cursor no. El orden lo resuelve PostgreSQL sobre la tabla; no se añade índice nuevo hasta que el volumen o el panel lo pidan.
7. **Pruebas contra PostgreSQL real.** El lease, el bloqueo y la clave de orden se comprueban con el motor que se usa en producción, no con un proveedor en memoria.

## 3. Alternativas descartadas

- **Ack sin número de entrega.** Aceptaría el cierre de una entrega caducada y podría aplicar dos veces el mismo trabajo; el testigo es lo que cierra la carrera.
- **Pasar a muerto en el propio nack.** El contrato dice que el paso ocurre al disponer y la espera da margen a un reintento manual; adelantarlo en el nack cambiaría la semántica del reintento.
- **Paginación por offset.** Con altas concurrentes, `OFFSET` repite o salta filas; el cursor de clave es estable y ordena por la misma clave que filtra sin memoria intermedia por página.
- **Cursor solo con el identificador.** Dos eventos pueden compartir instante de publicación; la clave completa evita el salto.
- **Consultar con bloqueo.** Una lectura no necesita esperar a un ack ni bloquear el reclamo.

## 4. Consecuencias

- El ciclo completo queda cerrado por API: publicar, reclamar, ackear con hash y consultar; tras cinco nacks el evento queda `fallido` con `intentos = 5` y el motivo del último fallo, y el siguiente reclamo lo aparta en `muerto`.
- Un cliente que reenvíe un ack o un nack de una entrega que no es la vigente recibe 409 y no toca nada.
- El listado no repite ni salta filas al paginar: el cursor continúa estrictamente después de la clave de la última fila entregada.
- El historial de un evento es su contador `intentos` y el resultado vigente (el motivo del último nack). No hay tabla de intentos; si el panel necesita el detalle de cada entrega, será un ítem propio y una tabla nueva.
- Las pruebas de integración siguen necesitando Docker y la misma imagen de PostgreSQL que compose.
- El contrato declara el 400 de `GET /v1/eventos/{id}` para el identificador mal formado, además del 404 del válido inexistente.
