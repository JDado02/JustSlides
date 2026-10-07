<#
.SYNOPSIS
  Costruisce artifacts\JustSlides-Setup-<versione>.exe (Inno Setup). Ripetibile, senza passi manuali.
.DESCRIPTION
  1. legge la versione dai csproj (App e PptHost devono coincidere);
  2. dotnet publish di App e PptHost nella stessa cartella di staging (artifacts\staging);
  3. controlla che ci siano exe, PptHost, runtime e plugin di VLC;
  4. scarica (una volta) il runtime .NET Desktop 10 x64 ufficiale in installer\prereq, ne verifica la firma Microsoft;
  5. lancia iscc.
  La firma e' spenta: con -SignCommand '<comando signtool con $f>' firma exe e Setup.
#>
[CmdletBinding()]
param(
    [string]$Iscc,
    [string]$SignCommand,
    [switch]$SkipRuntimeDownload
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Get-ProjVersion($path) {
    $m = [regex]::Match((Get-Content $path -Raw), '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $m.Success) { throw "Version non trovata in $path" }
    $m.Groups[1].Value
}
$verApp  = Get-ProjVersion 'src\Regia.App\Regia.App.csproj'
$verHost = Get-ProjVersion 'src\Regia.PptHost\Regia.PptHost.csproj'
if ($verApp -ne $verHost) { throw "Versioni diverse: App $verApp, PptHost $verHost" }
Write-Host "Versione: $verApp"

# Inno Setup
if (-not $Iscc) {
    $cand = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    $Iscc = $cand | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw "ISCC.exe non trovato. Installare: winget install JRSoftware.InnoSetup --source winget --scope user" }

# Staging
$artifacts = Join-Path $root 'artifacts'
$staging = Join-Path $artifacts 'staging'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null
dotnet publish src\Regia.App\Regia.App.csproj -c Release -o $staging --nologo -v q
if ($LASTEXITCODE) { throw 'publish App fallito' }
# CopyPptHost copia solo nella cartella di build, non in quella di publish: si pubblica anche PptHost (dll condivise identiche)
dotnet publish src\Regia.PptHost\Regia.PptHost.csproj -c Release -o $staging --nologo -v q
if ($LASTEXITCODE) { throw 'publish PptHost fallito' }

foreach ($f in 'JustSlides.exe','JustSlides.dll','JustSlides.PptHost.exe','JustSlides.PptHost.dll','JustSlides.PptHost.runtimeconfig.json','LibVLCSharp.dll') {
    if (-not (Test-Path (Join-Path $staging $f))) { throw "Manca nel pacchetto: $f" }
}
$vlcRoot = Join-Path $staging 'libvlc'
$vlc = Get-Item (Join-Path $vlcRoot 'win-x64\libvlc.dll') -ErrorAction SilentlyContinue
if (-not $vlc) { throw 'libvlc\win-x64\libvlc.dll non trovata nel pacchetto' }
if (-not (Test-Path (Join-Path $vlc.DirectoryName 'plugins'))) { throw 'Cartella plugins di VLC mancante' }
# L'app e' solo x64: via le altre architetture di VLC
Get-ChildItem $vlcRoot -Directory | Where-Object { $_.Name -ne 'win-x64' } | Remove-Item -Recurse -Force
Get-ChildItem $staging -Recurse -Include *.pdb | Remove-Item -Force

# Runtime .NET Desktop (incluso nell'installer, installato solo se manca sul PC)
$prereq = Join-Path $root 'installer\prereq'
New-Item -ItemType Directory -Force $prereq | Out-Null
$runtime = Get-ChildItem $prereq -Filter 'windowsdesktop-runtime-10.*-win-x64.exe' -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
if (-not $runtime) {
    if ($SkipRuntimeDownload) { throw 'Runtime non presente in installer\prereq' }
    Write-Host 'Scarico il runtime .NET Desktop 10 (x64) da Microsoft...'
    $tmp = Join-Path $prereq 'download.tmp'
    Invoke-WebRequest 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe' -OutFile $tmp -MaximumRedirection 5
    $sig = Get-AuthenticodeSignature $tmp
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        Remove-Item $tmp -Force; throw "Firma del runtime non valida ($($sig.Status))"
    }
    $name = "windowsdesktop-runtime-$((Get-Item $tmp).VersionInfo.ProductVersion -replace '[^\d\.].*','')-win-x64.exe"
    Move-Item $tmp (Join-Path $prereq $name) -Force
    $runtime = Get-Item (Join-Path $prereq $name)
}
Write-Host "Runtime incluso: $($runtime.Name)"

# Firma opzionale (spenta di default)
function Invoke-Sign($file) {
    if ($SignCommand) { & cmd /c ($SignCommand -replace '\$f', "`"$file`""); if ($LASTEXITCODE) { throw "Firma fallita: $file" } }
}
Invoke-Sign (Join-Path $staging 'JustSlides.exe')
Invoke-Sign (Join-Path $staging 'JustSlides.PptHost.exe')

& $Iscc "/DAppVersion=$verApp" "/DStagingDir=$staging" "/DRuntimeFile=$($runtime.FullName)" "/DOutDir=$artifacts" 'installer\JustSlides.iss'
if ($LASTEXITCODE) { throw 'iscc fallito' }
$setup = Join-Path $artifacts "JustSlides-Setup-$verApp.exe"
Invoke-Sign $setup
$h = (Get-FileHash $setup -Algorithm SHA256).Hash
Write-Host ("Pronto: {0} ({1} MB)`nSHA256: {2}" -f $setup, [math]::Round((Get-Item $setup).Length / 1MB), $h)
