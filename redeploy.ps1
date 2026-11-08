# Script untuk rebuild dan deploy ulang container FrogFrame saat ada perubahan kode
Write-Host "Mempublikasikan aplikasi (dotnet publish)..." -ForegroundColor Cyan
dotnet publish src/Apps.Sample/Apps.Sample.csproj -c Release -o ./publish

if ($LASTEXITCODE -ne 0) {
    Write-Host "Gagal melakukan publish aplikasi." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Membangun ulang (rebuild) dan memperbarui container frogframe-app..." -ForegroundColor Cyan
docker compose up -d --build app

if ($LASTEXITCODE -eq 0) {
    Write-Host "Berhasil! Aplikasi telah diperbarui dan berjalan di http://localhost:5080" -ForegroundColor Green
} else {
    Write-Host "Build gagal. Silakan periksa pesan error di atas." -ForegroundColor Red
}
