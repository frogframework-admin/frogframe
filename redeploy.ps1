# Script to rebuild and redeploy the FrogFrame container upon code changes
Write-Host "Publishing application (dotnet publish)..." -ForegroundColor Cyan
dotnet publish src/Apps.Erp/Apps.Erp.csproj -c Release -o ./publish

if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to publish application." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Rebuilding and updating frogframe-app container..." -ForegroundColor Cyan
docker compose up -d --build app

if ($LASTEXITCODE -eq 0) {
    Write-Host "Success! Application updated and running at http://localhost:5080" -ForegroundColor Green
} else {
    Write-Host "Build failed. Please check error output above." -ForegroundColor Red
}
