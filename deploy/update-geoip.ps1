# Aggiorna il database DB-IP Lite City usato da MainSite per la mappa delle
# richieste. Nessuna chiave/account: il file è pubblico e gratuito (CC BY 4.0).
#
# Uso (dalla root del repo):
#   .\deploy\update-geoip.ps1
#
# Dopo l'aggiornamento, in produzione riavvia il sito per riaprire il database.

param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\MainSite\App_Data\geoip")
)

$ErrorActionPreference = "Stop"
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force $Destination | Out-Null

$tmp = Join-Path $env:TEMP "dbip-city-lite.mmdb.gz"
$downloaded = $false
foreach ($offset in 0, 1) {
    $month = (Get-Date).ToUniversalTime().AddMonths(-$offset).ToString("yyyy-MM")
    $url = "https://download.db-ip.com/free/dbip-city-lite-$month.mmdb.gz"
    & curl.exe -fsSL --retry 3 --connect-timeout 15 $url -o $tmp
    if ($LASTEXITCODE -eq 0) {
        $downloaded = $true
        break
    }
}

if (-not $downloaded) {
    throw "Download DB-IP Lite fallito per il mese corrente e quello precedente."
}

$target = Join-Path $Destination "dbip-city-lite.mmdb"
$source = [IO.File]::OpenRead($tmp)
try {
    $output = [IO.File]::Create($target)
    try {
        $gzip = [IO.Compression.GZipStream]::new($source, [IO.Compression.CompressionMode]::Decompress)
        try {
            $gzip.CopyTo($output)
        }
        finally {
            $gzip.Dispose()
        }
    }
    finally {
        $output.Dispose()
    }
}
finally {
    $source.Dispose()
}

Remove-Item $tmp -Force
Write-Host "DB-IP Lite aggiornato: $target"
