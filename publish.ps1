# Genera il docker-compose dalla soluzione Aspire e lo rende pronto al deploy.
#
#   .\publish.ps1            -> output in .\out
#   .\publish.ps1 -Output X  -> output nella cartella X
#
# Fa tre cose:
#   1. aspire publish  -> docker-compose.yaml + .env
#   2. copia il contesto di build di Caddy (Dockerfile + Caddyfile) accanto
#      al compose, perche' la publish da sola non lo porta con se'.
#   3. costruisce l'immagine docker del sito (matteofabbri-site:latest),
#      quella che nel .env va in SITE_IMAGE.
param(
    [string]$Output = "out"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

aspire publish -o $Output
if ($LASTEXITCODE -ne 0) {
    Write-Error "aspire publish fallita (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

# Immagine del sito (richiede Docker in esecuzione).
dotnet publish MainSite\MainSite.csproj /t:PublishContainer `
    /p:ContainerRepository=matteofabbri-site /p:ContainerImageTag=latest -v q
if ($LASTEXITCODE -ne 0) {
    Write-Error "build dell'immagine del sito fallita (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

# Contesto di build di Caddy accanto al compose (sovrascrive se gia' presente).
Copy-Item -Recurse -Force "MatteoFabbri.org.AppHost\caddy" (Join-Path $Output "caddy")

Write-Host ""
Write-Host "Fatto. Output in: $Output" -ForegroundColor Green
Write-Host "Prossimi passi:"
Write-Host "  1. compila $Output\.env (SITE_IMAGE=matteofabbri-site:latest, SITE_PORT=8080, ...)"
Write-Host "  2. porta l'immagine sul server se e' remoto:"
Write-Host "       docker save matteofabbri-site:latest | ssh server docker load"
Write-Host "  3. sul server:  docker compose up -d --build"
