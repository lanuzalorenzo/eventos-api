# syntax=docker/dockerfile:1
# Multi-stage, imagen reducida y usuario no privilegiado.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# La configuración y los csproj van primero para aprovechar la caché de capas.
# GitHub Packages exige token incluso para leer paquetes públicos: entra como
# secreto de BuildKit y no queda en ninguna capa de la imagen.
COPY ["NuGet.config", "."]
COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["src/EventosApi.Api/EventosApi.Api.csproj", "src/EventosApi.Api/"]
COPY ["src/EventosApi.Domain/EventosApi.Domain.csproj", "src/EventosApi.Domain/"]
COPY ["src/EventosApi.Application/EventosApi.Application.csproj", "src/EventosApi.Application/"]
COPY ["src/EventosApi.Infrastructure/EventosApi.Infrastructure.csproj", "src/EventosApi.Infrastructure/"]
RUN --mount=type=secret,id=github_token \
    GH_PACKAGES_TOKEN="$(cat /run/secrets/github_token)" \
    dotnet restore "src/EventosApi.Api/EventosApi.Api.csproj"

COPY . .
RUN dotnet publish "src/EventosApi.Api/EventosApi.Api.csproj" -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Parches del sistema operativo base en la construcción, sin listas de APT en la imagen final.
RUN apt-get update \
    && apt-get -y upgrade \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

USER $APP_UID

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

ENTRYPOINT ["dotnet", "EventosApi.Api.dll"]
