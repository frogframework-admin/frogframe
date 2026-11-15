# ==========================================
# Runtime: Menggunakan image aspnet:10.0 lokal dan file hasil publish
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install Google Chrome for server-side PDF rendering
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl gnupg ca-certificates && \
    curl -fsSL https://dl.google.com/linux/linux_signing_key.pub | gpg --dearmor -o /usr/share/keyrings/google-chrome.gpg && \
    echo "deb [arch=amd64 signed-by=/usr/share/keyrings/google-chrome.gpg] http://dl.google.com/linux/chrome/deb/ stable main" > /etc/apt/sources.list.d/google-chrome.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends google-chrome-stable && \
    rm -rf /var/lib/apt/lists/*

# Port default ASP.NET Core di .NET 8+ adalah 8080
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

# Salin hasil publish aplikasi
COPY ./publish .

# Jalankan dengan non-root user bawaan .NET demi keamanan
USER $APP_UID

ENTRYPOINT ["dotnet", "Apps.Erp.dll"]
