# Etapa 1: Build y Restore con SDK .NET 8
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copiar solución y csproj para optimizar caché de capas Docker
COPY Fixia.Payments.sln .
COPY src/Fixia.Payments.Domain/Fixia.Payments.Domain.csproj src/Fixia.Payments.Domain/
COPY src/Fixia.Payments.Application/Fixia.Payments.Application.csproj src/Fixia.Payments.Application/
COPY src/Fixia.Payments.Infrastructure/Fixia.Payments.Infrastructure.csproj src/Fixia.Payments.Infrastructure/
COPY src/Fixia.Payments.Api/Fixia.Payments.Api.csproj src/Fixia.Payments.Api/
COPY tests/Fixia.Payments.UnitTests/Fixia.Payments.UnitTests.csproj tests/Fixia.Payments.UnitTests/

RUN dotnet restore Fixia.Payments.sln

# Copiar el código fuente completo
COPY src/ src/
COPY tests/ tests/

# Compilación y publicación del proyecto
RUN dotnet publish src/Fixia.Payments.Api/Fixia.Payments.Api.csproj -c Release -o /app/publish --no-restore

# Etapa 2: Runtime liviano ASP.NET Core 8
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_ENVIRONMENT=Development

COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "Fixia.Payments.Api.dll"]
