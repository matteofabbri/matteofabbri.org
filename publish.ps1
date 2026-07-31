# Genera il docker-compose dalla soluzione Aspire e lo rende pronto al deploy.
#
#   .\publish.ps1            -> output in .\out
#   .\publish.ps1 -Output X  -> output nella cartella X
#
# Fa tre cose:
#   1. aspire publish  -> docker-compose.yaml + .env
#   2. copia il contesto di build di Caddy (Dockerfile + Caddyfile) accanto
#      al compose, perche' la publish da sola non lo porta con se'.
#   3. costruisce le immagini docker dei tre siti (matteofabbri-site,
#      matteofabbri-theqult, matteofabbri-maddalena, tutte :latest), quelle
#      che nel .env vanno in SITE_IMAGE/THEQULT_IMAGE/MADDALENA_IMAGE.
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

# Immagini dei tre siti (richiede Docker in esecuzione). I nomi qui sotto sono
# quelli da usare in SITE_IMAGE/THEQULT_IMAGE/MADDALENA_IMAGE nel .env.
$images = @(
    @{ Project = "MainSite\MainSite.csproj"; Repository = "matteofabbri-site" }
    @{ Project = "TheQult\TheQult.csproj"; Repository = "matteofabbri-theqult" }
    @{ Project = "Maddalena\Maddalena.csproj"; Repository = "matteofabbri-maddalena" }
)
foreach ($image in $images) {
    dotnet publish $image.Project /t:PublishContainer `
        /p:ContainerRepository=$($image.Repository) /p:ContainerImageTag=latest -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Error "build dell'immagine $($image.Repository) fallita (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
}

# Contesti di build accanto al compose: Caddy e tileserver (l'immagine che si
# scarica la mappa del mondo da sola). La destinazione si azzera prima: con
# -Force su una cartella gia' esistente Copy-Item copierebbe DENTRO di essa
# (out\caddy\caddy) lasciando al primo livello i file della publish precedente.
foreach ($ctx in @("caddy", "tileserver")) {
    $ctxOut = Join-Path $Output $ctx
    if (Test-Path $ctxOut) { Remove-Item -Recurse -Force $ctxOut }
    Copy-Item -Recurse (Join-Path "MatteoFabbri.org.AppHost" $ctx) $ctxOut
}

Write-Host ""
Write-Host "Fatto. Output in: $Output" -ForegroundColor Green
Write-Host "Prossimi passi:"
Write-Host "  1. compila $Output\.env (SITE_IMAGE/THEQULT_IMAGE/MADDALENA_IMAGE=matteofabbri-*:latest, *_PORT=8080, ...)"
Write-Host "     fra questi VALHALLA_TILE_URLS: l'estratto OSM da cui costruire i percorsi,"
Write-Host "     es. https://download.geofabrik.de/europe/italy-latest.osm.pbf (lasciarlo vuoto = nessun percorso)"
Write-Host "     PLANET_URL invece puo' restare VUOTO: tileserver si scarica da solo l'ultima"
Write-Host "     mappa del mondo (~137 GB) al primo avvio. Si valorizza solo per fissare una versione."
Write-Host "  2. porta le tre immagini sul server se e' remoto:"
Write-Host "       docker save matteofabbri-site matteofabbri-theqult matteofabbri-maddalena | ssh server docker load"
Write-Host "  3. sul server:  docker compose up -d --build"
