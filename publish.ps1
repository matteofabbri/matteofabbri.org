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
#      che nel .env vanno in SITE_IMAGE/THEQULT_IMAGE/MADDALENA_IMAGE. Il sito
#      riceve un layer in più con ffmpeg (serve a yt-dlp).
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
# Il sito ha in più un layer con ffmpeg (deploy/site-ffmpeg.Dockerfile): yt-dlp
# senza ffmpeg non può unire traccia video e audio. L'SDK pubblica l'immagine
# con il tag temporaneo :sdk, poi la build del Dockerfile produce :latest.
$images = @(
    @{ Project = "MainSite\MainSite.csproj"; Repository = "matteofabbri-site"; LayerDockerfile = "deploy\site-ffmpeg.Dockerfile" }
    @{ Project = "TheQult\TheQult.csproj"; Repository = "matteofabbri-theqult" }
    @{ Project = "Maddalena\Maddalena.csproj"; Repository = "matteofabbri-maddalena" }
)
foreach ($image in $images) {
    $tag = if ($image.LayerDockerfile) { "sdk" } else { "latest" }

    # -r linux-x64: un contenitore e' Linux, e senza RID il publish copia i
    # nativi di ogni piattaforma (ONNX da Sperone: iOS, Android, macOS, Windows,
    # ~175 MB inutili). Con il RID resta solo il .so linux-x64.
    $publishArgs = @(
        $image.Project
        "/t:PublishContainer"
        "/p:ContainerRepository=$($image.Repository)"
        "/p:ContainerImageTag=$tag"
        "-r"
        "linux-x64"
        "-v"
        "q"
    )

    dotnet publish @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "build dell'immagine $($image.Repository) fallita (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }

    if ($image.LayerDockerfile) {
        docker build -f $image.LayerDockerfile -t "$($image.Repository):latest" .
        if ($LASTEXITCODE -ne 0) {
            Write-Error "layer finale di $($image.Repository) fallito (exit $LASTEXITCODE)"
            exit $LASTEXITCODE
        }
    }
}

# Contesti di build accanto al compose: Caddy (l'immagine su misura col modulo
# layer4). La destinazione si azzera prima: con
# -Force su una cartella gia' esistente Copy-Item copierebbe DENTRO di essa
# (out\caddy\caddy) lasciando al primo livello i file della publish precedente.
foreach ($ctx in @("caddy")) {
    $ctxOut = Join-Path $Output $ctx
    if (Test-Path $ctxOut) { Remove-Item -Recurse -Force $ctxOut }
    Copy-Item -Recurse (Join-Path "MatteoFabbri.org.AppHost" $ctx) $ctxOut
}

Write-Host ""
Write-Host "Fatto. Output in: $Output" -ForegroundColor Green
Write-Host "Prossimi passi:"
Write-Host "  1. compila $Output\.env (SITE_IMAGE/THEQULT_IMAGE/MADDALENA_IMAGE=matteofabbri-*:latest, *_PORT=8080, ...)"
Write-Host "  2. porta le tre immagini sul server se e' remoto:"
Write-Host "       docker save matteofabbri-site matteofabbri-theqult matteofabbri-maddalena | ssh server docker load"
Write-Host "  3. sul server:  docker compose up -d --build --remove-orphans"
Write-Host "     --remove-orphans butta anche i container dei servizi tolti dal"
Write-Host "     compose; volumi e immagini orfani li pulisce da solo il deploy"
Write-Host "     GitHub (vedi deploy/README.md, sezione 2-ter)."
