# Fixia ms-payments (.NET 8)

Microservicio de procesamiento y gestión de pagos para la plataforma **Fixia**, desarrollado con **.NET 8** siguiendo los principios de **Clean Architecture** y **Domain-Driven Design (DDD)**.

## 🏛️ Arquitectura de Capas

El microservicio está organizado siguiendo las capas de Clean Architecture:

```
ms-payments/
├── src/
│   ├── Fixia.Payments.Domain/         # Entidades, Value Objects, Enums, Reglas de negocio puras
│   ├── Fixia.Payments.Application/    # Casos de uso, DTOs, Mappings, Interfaces (Puertos)
│   ├── Fixia.Payments.Infrastructure/ # EF Core (Npgsql), Repositorios, Kafka EventPublisher, Mock Gateway
│   └── Fixia.Payments.Api/            # Controladores REST, Middlewares, Swagger, Health Checks
├── tests/
│   └── Fixia.Payments.UnitTests/      # Pruebas unitarias con xUnit, FluentAssertions y Moq
├── Dockerfile                         # Build multi-stage optimizado para .NET 8
└── Fixia.Payments.sln
```

## 🚀 Endpoints Principales

Todos los endpoints se encuentran enrutados bajo `/api/payments`:

| Método | Endpoint | Descripción |
| :--- | :--- | :--- |
| `POST` | `/api/payments` | Crea una intención o registro de pago para una solicitud de servicio. |
| `POST` | `/api/payments/{id}/process` | Procesa el cobro a través de la pasarela de pagos. |
| `GET` | `/api/payments/{id}` | Obtiene el detalle de un pago y su historial de transacciones. |
| `GET` | `/api/payments/service-request/{serviceRequestId}` | Obtiene el pago asociado a una solicitud de servicio. |
| `POST` | `/api/payments/{id}/refund` | Ejecuta el reembolso de un pago previamente completado. |
| `GET` | `/api/payments/health` | Health check del microservicio. |
| `GET` | `/api/payments/swagger` | Documentación interactiva OpenAPI/Swagger. |

## 🧪 Pruebas Unitarias

Para ejecutar las pruebas en un entorno contenerizado:

```bash
docker run --rm -v $(pwd):/app -w /app mcr.microsoft.com/dotnet/sdk:8.0 dotnet test Fixia.Payments.sln
```
