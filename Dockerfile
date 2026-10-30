# syntax=docker/dockerfile:1

# ==========================================
# Stage 1: Build & Publish
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Salin file konfigurasi build & dependensi
COPY ["Directory.Build.props", "./"]
COPY ["nuget.config", "./"]
COPY ["global.json", "./"]

# Salin file .csproj tiap proyek untuk optimasi caching layer saat restore NuGet
COPY ["src/Framework.Core/Framework.Core.csproj", "src/Framework.Core/"]
COPY ["src/Framework.Persistence.PostgreSql/Framework.Persistence.PostgreSql.csproj", "src/Framework.Persistence.PostgreSql/"]
COPY ["src/Framework.Web/Framework.Web.csproj", "src/Framework.Web/"]
COPY ["src/Apps.Sample/Apps.Sample.csproj", "src/Apps.Sample/"]

# Restore dependensi
RUN dotnet restore "src/Apps.Sample/Apps.Sample.csproj"

# Salin source code
COPY ["src/", "src/"]

# Publish aplikasi (Release)
WORKDIR "/src/src/Apps.Sample"
RUN dotnet publish "Apps.Sample.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ==========================================
# Stage 2: Runtime
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Port default ASP.NET Core di .NET 8+ adalah 8080
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

# Salin hasil publish dari build stage
COPY --from=build /app/publish .

# Jalankan dengan non-root user bawaan .NET demi keamanan
USER $APP_UID

ENTRYPOINT ["dotnet", "Apps.Sample.dll"]

