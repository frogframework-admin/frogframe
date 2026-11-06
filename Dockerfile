# ==========================================
# Runtime: Menggunakan image aspnet:10.0 lokal dan file hasil publish
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Port default ASP.NET Core di .NET 8+ adalah 8080
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

# Salin hasil publish aplikasi
COPY ./publish .

# Jalankan dengan non-root user bawaan .NET demi keamanan
USER $APP_UID

ENTRYPOINT ["dotnet", "Apps.Sample.dll"]
