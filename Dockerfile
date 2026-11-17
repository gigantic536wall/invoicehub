# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, so the package layer is cached until a project file changes.
COPY Directory.Build.props .editorconfig ./
COPY src/InvoiceHub.Domain/InvoiceHub.Domain.csproj src/InvoiceHub.Domain/
COPY src/InvoiceHub.Api/InvoiceHub.Api.csproj src/InvoiceHub.Api/
RUN dotnet restore src/InvoiceHub.Api/InvoiceHub.Api.csproj

COPY src/ src/
RUN dotnet publish src/InvoiceHub.Api/InvoiceHub.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Database__MigrateOnStartup=true
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "InvoiceHub.Api.dll"]
