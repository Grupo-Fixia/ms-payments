# Fixia ms-payments (.NET 8)

Microservicio de procesamiento y gestión de pagos para la plataforma **Fixia**, desarrollado con **.NET 8** siguiendo los principios de **Clean Architecture** y **Domain-Driven Design (DDD)**.

## 🏛️ Arquitectura de Capas

El microservicio está organizado siguiendo las capas de Clean Architecture:

```
ms-payments/
├── src/
│   ├── Fixia.Payments.Domain/         # Entidades, Value Objects, Enums, Reglas de negocio puras
│   ├── Fixia.Payments.Application/    # Casos de uso, DTOs, Mappings, Interfaces (Puertos), eventos de negocio
│   ├── Fixia.Payments.Infrastructure/ # EF Core (Npgsql) y migraciones, Repositorios, Outbox y relay a Kafka, Mock Gateway
│   └── Fixia.Payments.Api/            # Controladores REST, seguridad (JWT y clave interna), Middlewares, Swagger, Health Checks
├── tests/
│   └── Fixia.Payments.UnitTests/      # Pruebas con xUnit, FluentAssertions y Moq; API con WebApplicationFactory; PostgreSQL opcional
├── docs/events/                       # Contrato de los eventos pago.* (GC-477)
├── Dockerfile                         # Build multi-stage optimizado para .NET 8
└── Fixia.Payments.sln
```

## 🚀 Endpoints Principales

| Método | Endpoint | Quién | Descripción |
| :--- | :--- | :--- | :--- |
| `POST` | `/internal/payments` | Otro microservicio (`X-Internal-Api-Key`) | Registra el pago de una solicitud de servicio. Idempotente: el mismo pedido repetido responde 200 con el pago existente; otros datos para la misma solicitud, 409. No pasa por Traefik. |
| `POST` | `/api/payments/{id}/process` | El pagador del pago | Procesa el cobro a través de la pasarela de pagos. |
| `GET` | `/api/payments/{id}` | Pagador, prestador o `ROLE_ADMIN` | Obtiene el detalle de un pago y su historial de transacciones. |
| `GET` | `/api/payments/service-request/{serviceRequestId}` | Pagador, prestador o `ROLE_ADMIN` | Obtiene el pago asociado a una solicitud de servicio. |
| `POST` | `/api/payments/{id}/refund` | `ROLE_ADMIN` | Ejecuta el reembolso de un pago previamente completado. |
| `GET` | `/api/payments/health` | Público | Health check del microservicio (también `/actuator/health`). |
| `GET` | `/api/payments/swagger` | Público | Documentación interactiva OpenAPI/Swagger (botón *Authorize* para el token). |

`POST /api/payments` ya no existe: la creación la hace otro microservicio por la ruta interna (GC-477).

## 🔐 Seguridad (GC-477)

- Las rutas `/api/payments/**` exigen el **access token de ms-users** (`Authorization: Bearer …`): firma RS256 con `JWT_PUBLIC_KEY`, emisor `JWT_ISSUER`, audiencia `JWT_AUDIENCE` y expiración (tolerancia de 5 s). Los roles vienen en el claim `roles` (`ROLE_CLIENT`, `ROLE_PROFESSIONAL`, `ROLE_COMPANY`, `ROLE_ADMIN`) y el usuario en `sub` (`usr_<uuid>`), igual que en ms-services.
- **401** sin token o con token inválido (vencido, de otro emisor, otra audiencia, mal firmado o `sub` sin `usr_`); el cuerpo no explica el motivo.
- **403** cuando se participa en el pago pero no se tiene permiso (el prestador que intenta cobrar, alguien que no es administrador y pide un reembolso).
- **404** para un pago ajeno, igual que para uno inexistente: no se revela que existe.
- **409** si dos operaciones cambian el mismo pago a la vez (bloqueo optimista con `xmin`) o si la solicitud ya tiene un pago con otros datos.
- Las rutas `/internal/**` exigen `X-Internal-Api-Key` (`INTERNAL_API_KEY`, comparación en tiempo constante). Sin clave configurada no aceptan ninguna petición.
- Sin `JWT_PUBLIC_KEY`, en `Development` el servicio arranca con una clave efímera y todas las rutas protegidas responden 401; en cualquier otro ambiente no arranca.
- No se consulta todavía la revocación de tokens en ms-users (`/internal/tokens/{jti}/status`), como sí hace ms-services.

## ⚙️ Configuración

| Variable | Uso | Valor por defecto |
| :--- | :--- | :--- |
| `ConnectionStrings__PaymentsDb` | PostgreSQL de pagos | `Host=localhost;Port=5433;Database=payments_db;…` |
| `Kafka__BootstrapServers` | Broker para el relay del Outbox | `localhost:29092` |
| `JWT_PUBLIC_KEY` | Clave pública PEM (X.509) de ms-users. Obligatoria fuera de `Development` | — |
| `JWT_ISSUER` / `JWT_AUDIENCE` | Emisor y audiencia esperados | `https://auth.fixia.com` / `https://api.fixia.com` |
| `INTERNAL_API_KEY` | Clave compartida para `/internal/**` | — (sin ella la ruta interna rechaza todo) |
| `Database__ApplyMigrationsOnStartup` | Aplicar las migraciones al arrancar | `true` |
| `Outbox__RelayEnabled` / `Outbox__PollIntervalMilliseconds` / `Outbox__BatchSize` | Relay del Outbox | `true` / `2000` / `50` |

## 🗄️ Migraciones (GC-477)

El esquema se crea con **migraciones versionadas de EF Core** (`src/Fixia.Payments.Infrastructure/Persistence/Migrations`), ya no con `EnsureCreated()`. La primera, `InitialCreate`, crea `payments` (con un pago por solicitud), `payment_transactions` y `outbox_events`.

```bash
dotnet tool restore                                   # instala dotnet-ef 8.0.11 (herramienta local)
dotnet ef migrations add <Nombre> -p src/Fixia.Payments.Infrastructure -s src/Fixia.Payments.Infrastructure -o Persistence/Migrations
dotnet ef migrations script -p src/Fixia.Payments.Infrastructure -s src/Fixia.Payments.Infrastructure --idempotent
```

- Por defecto el servicio aplica las migraciones pendientes al arrancar (reintenta si la base aún no responde).
- Para QA se puede desactivar con `Database__ApplyMigrationsOnStartup=false` y aplicarlas antes del despliegue con un *bundle*: `dotnet ef migrations bundle -p src/Fixia.Payments.Infrastructure -s src/Fixia.Payments.Infrastructure -o efbundle` y luego `./efbundle --connection "<cadena>"`.
- Si la base quedó con las tablas que creaba `EnsureCreated()` en la versión anterior: vacías, se reemplazan solas; con datos, el servicio no arranca y lo explica en el log (no borra nada).
- La prueba `MigrationsTests` falla si alguien cambia el modelo y olvida generar la migración.

## 📨 Eventos (GC-477)

Los eventos `pago.*` se escriben en el **Outbox** (`outbox_events`) en la misma transacción que el cambio del pago y un relay en segundo plano los publica en `fixia.payments.events`, con el sobre de ms-services. Si Kafka está caído, el pago se registra igual y el evento sale cuando vuelve. Contrato completo en [`docs/events/README.md`](docs/events/README.md).

## 🧪 Pruebas

```bash
dotnet test Fixia.Payments.sln
```

Las pruebas que necesitan PostgreSQL real (migraciones sobre base vacía, unicidad, bloqueo optimista, relay del Outbox y esquema anterior) se omiten si no está la variable `FIXIA_PAYMENTS_TEST_PG`. Para correrlas, con un PostgreSQL 16 disponible (por ejemplo el `fixia-db-payments` del compose):

```bash
FIXIA_PAYMENTS_TEST_PG="Host=localhost;Port=5433;Username=user;Password=password;Database=postgres" dotnet test Fixia.Payments.sln
```

Cada prueba crea su propia base `payments_test_*` y la borra al terminar.

En un entorno contenerizado:

```bash
docker run --rm -v $(pwd):/app -w /app mcr.microsoft.com/dotnet/sdk:8.0 dotnet test Fixia.Payments.sln
```
