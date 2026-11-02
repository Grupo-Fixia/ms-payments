# Contrato de eventos de ms-payments (GC-477, GC-486, GC-488)

Cómo publica `ms-payments` sus hechos de negocio en Kafka. Sigue el contrato de `ms-services`
(`ms-services/docs/events/README.md`, GC-436): mismo sobre, mismas cabeceras, mismo Outbox y mismas reglas del relay.
Aquí solo se documenta lo propio de pagos.

> **Alcance.** GC-477 y GC-486 llevaron los eventos al Outbox; GC-488 fija el contrato con **esquemas JSON
> versionados** (`schemas/`) y **ejemplos** del flujo de billetera (`examples/`), que una prueba automática valida junto
> con los eventos que el servicio escribe de verdad (`EventContractTests`). La billetera es **simulada** (GC-476): no hay
> dinero real.

## 1. Quién publica y cómo

| Paso | Qué pasa |
| --- | --- |
| 1 | El caso de uso cambia el pago y escribe el evento en `outbox_events` **en la misma transacción** (`SaveChangesAsync`). Si la transacción falla, el evento no existe |
| 2 | El relay (`OutboxRelay`, proceso en segundo plano del mismo servicio) toma lo pendiente y lo publica en Kafka |
| 3 | Solo después del acuse del broker marca el evento como `PUBLISHED` |

Antes de GC-477 el servicio publicaba directo después de guardar y, si Kafka fallaba, el evento se perdía. Ahora
ningún hecho se publica antes de confirmarse ni se pierde si Kafka está caído.

## 2. Tópico, clave y cabeceras

- **Tópico:** `fixia.payments.events` (lo crea el DevOps; 3 particiones como `fixia.services.service-requests`).
- **Clave del mensaje:** `aggregate.id`, el id del **pago**, como en el contrato de `ms-services` (GC-436) y como
  valida el lakehouse (GC-435). Todos los eventos de un pago caen en la misma partición y se leen en orden; como hay un
  solo pago por solicitud, también quedan en orden los de cada servicio. La relación con el servicio va en
  `correlationId`.
- **Cabeceras:** `eventId`, `eventType`, `schemaVersion` y `content-type: application/json`. Repiten datos del cuerpo.
- **Cuerpo:** el sobre de la sección 3, en JSON UTF-8.
- **Productor:** idempotente, `acks=all`, tiempo máximo por mensaje de 10 s.

## 3. Sobre (`envelope.v1.schema.json`)

| Campo | Tipo | Regla |
| --- | --- | --- |
| `eventId` | UUID | Se genera al escribir en el Outbox y no cambia en reintentos: es la clave de idempotencia de los consumidores |
| `eventType` | texto | Uno del catálogo (sección 4) |
| `schemaVersion` | entero ≥ 1 | Versión del esquema de `data` |
| `occurredAt` | instante UTC | Momento del hecho, en microsegundos (`2026-11-02T20:02:55.536580Z`); es el mismo que queda en la base |
| `producer` | `ms-payments` | |
| `aggregate.type` | `Payment` | El agregado es el pago |
| `aggregate.id` | UUID | Id del pago; también es la clave del mensaje |
| `aggregate.sequence` | entero ≥ 1 | Posición del hecho en la historia del pago: 1, 2, 3… sin huecos ni repetidos (restricción única `(aggregate_id, aggregate_sequence)`) |
| `correlationId` | UUID | Id de la solicitud de servicio, el mismo valor que usa `ms-services`. Enlaza los eventos de pagos con los del servicio |
| `actor.userId` | UUID o null | `users.id` de quien hizo la operación (el `sub` del token sin `usr_`); null si fue el sistema u otro microservicio |
| `actor.role` | `CLIENT`, `PROFESSIONAL`, `COMPANY`, `ADMIN` o `SYSTEM` | `ADMIN` es propio de pagos (reembolsos) |
| `data` | objeto | Datos del hecho (sección 4) |

Los importes viajan como `{"amount": "105000.00", "currency": "COP"}`: el valor es **texto** con dos decimales.

## 4. Catálogo (versión 1)

Esquemas en `schemas/` (draft 2020-12): `common.v1`, `envelope.v1` y uno por evento, que extiende el sobre y fija
`eventType`, `schemaVersion` y `data`. Usan `$id` propios (`https://fixia.local/schemas/payments/…`) para no chocar con
los de `ms-services` en el lakehouse.

| `eventType` | Cuándo | `actor` | `data` (obligatorio · *opcional*) |
| --- | --- | --- | --- |
| `pago.creado` | Se registra el pago: por la ruta interna o, con billetera, al contratar (GC-484). Un reintento con los mismos datos no publica otro | `SYSTEM` | `paymentId`, `serviceRequestId`, `clientId`, `providerId`, `amount`, `method`, `status=Pending` · *`contractId`, `breakdown`* |
| `pago.procesado` | Con billetera: el total quedó **retenido** en la billetera del cliente al contratar (GC-484) | `SYSTEM` | `paymentId`, `serviceRequestId`, `contractId`, `clientId`, `providerId`, `amount`, `breakdown`, `status=Processing` |
| `pago.completado` | Con billetera: se cobró lo retenido al confirmar el cierre (GC-485). Sin billetera: la pasarela aprobó el cobro | `SYSTEM` o el pagador | `paymentId`, `serviceRequestId`, `clientId`, `providerId`, `amount`, `transactionReference`, `status=Completed` · *`contractId`, `breakdown`* |
| `pago.fallido` | Con billetera: no alcanzó el saldo al contratar (no se retiene nada). Sin billetera: la pasarela rechazó | `SYSTEM` o el pagador | `paymentId`, `serviceRequestId`, `clientId`, `providerId`, `amount`, `failureReason`, `status=Failed` · *`contractId`, `breakdown`* |
| `pago.reembolsado` | Un administrador devuelve el pago (GC-487) | `ADMIN` | `paymentId`, `serviceRequestId`, `amount`, `refundReference`, `reason`, `kind`, `status=Refunded` · *`breakdown`* |

- **`breakdown`** (pagos con billetera): `price` (lo que recibe el prestador), `commission` (de Fixia, a cargo del
  cliente), `rate` (`"0.1500"` = 15 %) y `ruleId` (la regla de `commission_rules` aplicada). Siempre
  `amount = price + commission`.
- **`kind`** en `pago.reembolsado`: `RELEASE` (se liberó lo retenido; no se había cobrado), `REVERSAL` (se revirtió un
  pago con billetera ya cobrado: prestador y Fixia devuelven, el cliente recibe) o `GATEWAY` (reembolso por pasarela de
  un pago sin billetera).
- **Sin evento:** si el cliente **objeta el cierre** no se cobra ni se publica nada: el pago sigue `Processing`
  (retenido) hasta que se cierre o un administrador lo libere. **Un servicio contratado no se puede cancelar**
  (decisión del 2026-11-02), así que no hay liberación automática por cancelación.

### Ejemplos del flujo con billetera (`examples/flujo-billetera/`)

Un servicio de 105 000 COP con 15 % de comisión (total 120 750). Todos cumplen su esquema (prueba automática).

| Archivo | Paso |
| --- | --- |
| `01-pago.creado.json` | Se registra el pago al contratar |
| `02-pago.procesado.json` | Se retienen 120 750 al cliente |
| `02-alt-pago.fallido-sin-saldo.json` | Alternativa: no alcanzó el saldo |
| `03-pago.completado.json` | El cliente confirmó el cierre: prestador +105 000, Fixia +15 750 |
| `03-alt-pago.reembolsado-liberacion.json` | Alternativa: tras una objeción, un administrador libera lo retenido (`RELEASE`) |
| `04-pago.reembolsado-reverso.json` | Después del cobro, un administrador revierte el pago (`REVERSAL`) |

### Ejemplo (`pago.creado`, tal como llegó a Kafka en la prueba de GC-477)

Cabeceras: `eventId:e6161fce-…, eventType:pago.creado, schemaVersion:1, content-type:application/json` ·
Clave: `05717e69-811e-42db-8343-39fb9a16c99c` (el `aggregate.id`)

```json
{
  "eventId": "e6161fce-64bd-46b1-8d49-818dce5390b8",
  "eventType": "pago.creado",
  "schemaVersion": 1,
  "occurredAt": "2026-11-02T20:02:55.536580Z",
  "producer": "ms-payments",
  "aggregate": { "type": "Payment", "id": "05717e69-811e-42db-8343-39fb9a16c99c", "sequence": 1 },
  "correlationId": "11111111-1111-4111-8111-111111111111",
  "actor": { "userId": null, "role": "SYSTEM" },
  "data": {
    "paymentId": "05717e69-811e-42db-8343-39fb9a16c99c",
    "serviceRequestId": "11111111-1111-4111-8111-111111111111",
    "clientId": "22222222-2222-4222-8222-222222222222",
    "providerId": "33333333-3333-4333-8333-333333333333",
    "amount": { "amount": "105000.00", "currency": "COP" },
    "method": "CreditCard",
    "status": "Pending"
  }
}
```

## 5. Outbox (`outbox_events`, migración `InitialCreate`)

Mismas columnas que el de `ms-services`: `event_id` (PK), `event_type`, `schema_version`, `aggregate_type`,
`aggregate_id`, `aggregate_sequence`, `topic`, `message_key`, `payload` (el sobre completo, hasta 16000 caracteres),
`occurred_at`, y las de envío `status` (`PENDING`, `PUBLISHED`, `FAILED`), `attempts`, `next_attempt_at`,
`last_error` (sin el contenido del evento) y `published_at`. Índices: único `(aggregate_id, aggregate_sequence)` y
`(status, next_attempt_at)`.

**Relay (`OutboxRelay`):**

1. Cada ronda (2 s por defecto) toma hasta 50 eventos `PENDING` con `next_attempt_at` vencido, en orden de
   `occurred_at` y secuencia, con `FOR UPDATE SKIP LOCKED` (dos réplicas no publican el mismo).
2. No publica la secuencia *n + 1* de un pago mientras la *n* no esté `PUBLISHED`.
3. Marca `PUBLISHED` solo después del acuse. Si el proceso cae entre el acuse y la marca, el evento se republica con el
   mismo `eventId`: es una **reentrega permitida** (al menos una vez, no exactly-once). Los consumidores deduplican por
   `eventId`.
4. Si falla, suma un intento y reprograma con espera exponencial (2, 4, 8… s, máximo 5 min). Tras 10 intentos queda
   `FAILED`; se reactiva volviéndolo a `PENDING` (`UPDATE outbox_events SET status='PENDING', attempts=0,
   next_attempt_at=now() WHERE event_id=…`).
5. Se configura con `Outbox__RelayEnabled`, `Outbox__PollIntervalMilliseconds` y `Outbox__BatchSize`.

## 6. Recuperación ante fallos

| Situación | Qué pasa | Cómo se recupera | Verificado |
| --- | --- | --- | --- |
| La transacción de negocio falla | No hay evento | Nada que recuperar | Prueba `Service_ShouldWritePaymentAndEventInTheSameTransaction` |
| Kafka no está disponible | El pago se registra igual (201); el evento queda `PENDING` con el intento y el error | Al volver Kafka y vencer la espera, sale; el siguiente del mismo pago sale después | Prueba `Relay_ShouldPublishInOrder_AndRecoverAfterKafkaFails` y prueba manual con Kafka 3.9 real |
| El relay cae después del acuse | Reentrega con el mismo `eventId` | El consumidor deduplica | Por diseño |
| Un evento agota sus reintentos | Queda `FAILED` | Se corrige la causa y se vuelve a `PENDING` | Prueba `OutboxEventTests` |

## 7. Versionado y privacidad

- Mismas reglas que `ms-services`: agregar un campo opcional a `data` es compatible (misma versión); quitar,
  renombrar o cambiar el significado de un campo es una versión nueva. El historial no se reescribe.
- Los eventos no llevan tokens, claves, datos de tarjeta ni la respuesta cruda de la pasarela. Los logs del relay
  registran `eventId`, tipo e intento, nunca el contenido. Los esquemas tienen `additionalProperties: false`: un campo
  no previsto (por ejemplo un número de tarjeta) hace que el evento no cumpla el contrato.
- **Para el lakehouse (GC-435):** validar cada mensaje contra `schemas/<eventType>.v<schemaVersion>.schema.json` y
  deduplicar por `eventId`, igual que con los de `ms-services`.
