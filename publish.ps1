# Genera una release limpia de Echoplex en app\Echoplex-<versión>\ (con -Zip, además un .zip de la carpeta).
# La carpeta lleva Echoplex.exe con sus dependencias al lado (sin empaquetar en un único archivo):
#   Echoplex.exe, Echoplex.dll, CommunityToolkit.Mvvm.dll, NAudio.*.dll, TagLibSharp.dll, *.deps.json, *.runtimeconfig.json
#
#   .\publish.ps1                          -> ligera: usa el .NET 8 Desktop Runtime instalado en el sistema
#   .\publish.ps1 -Portable                -> autocontenida: funciona en cualquier PC sin instalar .NET (mucho más grande)
#   .\publish.ps1 -Version 2.3.0           -> fuerza la versión (por defecto, la <Version> de Echoplex.csproj)
#   .\publish.ps1 -Zip                     -> además, Echoplex-<versión>-win-x64.zip con toda la carpeta
#
# Firma (opcional; sin ella Windows SmartScreen avisa en otros PCs):
#   .\publish.ps1 -CertThumbprint <SHA1>   -> certificado de firma de código instalado (token USB, Certum, etc.)
#   .\publish.ps1 -PfxPath cert.pfx -PfxPassword ...   -> certificado en archivo (sirve para probar la firma)
#   -TimestampUrl                          -> servidor de sellado de tiempo (la firma sigue valiendo cuando caduque el certificado)
param(
    [string]$Version,
    [switch]$Portable,
    [switch]$Zip,
    [string]$CertThumbprint,
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'Echoplex.csproj'
if (-not $Version) {
    $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$name = if ($Portable) { "Echoplex-$Version-portable" } else { "Echoplex-$Version" }
$out = Join-Path $PSScriptRoot "app\$name"
$zipPath = "$out-win-x64.zip"
if (Test-Path $out) { throw "La release $name ya existe en $out. Sube la versión en Echoplex.csproj o pasa -Version." }

$common = @(
    '-c', 'Release', '-r', 'win-x64',
    '-p:PublishSingleFile=false',
    '-p:DebugType=none', '-p:DebugSymbols=false',
    '-p:GenerateDocumentationFile=false',
    "-p:Version=$Version",
    '-o', $out
)
if ($Portable) {
    dotnet publish $proj @common --self-contained true
} else {
    dotnet publish $proj @common --self-contained false
}
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló" }

# Firma: solo lo nuestro (Echoplex.exe y Echoplex.dll); las DLL de terceros van tal cual las publica su autor.
if ($CertThumbprint -or $PfxPath) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw "No se encuentra signtool.exe (viene con el Windows SDK)." }
    $cert = if ($CertThumbprint) { @('/sha1', $CertThumbprint) } else { @('/f', $PfxPath) + $(if ($PfxPassword) { @('/p', $PfxPassword) } else { @() }) }
    $files = @('Echoplex.exe', 'Echoplex.dll') | ForEach-Object { Join-Path $out $_ }
    & $signtool sign /fd sha256 /tr $TimestampUrl /td sha256 /d 'Echoplex' @cert @files
    if ($LASTEXITCODE -ne 0) { throw "La firma falló" }
    Write-Host "Firmado: Echoplex.exe, Echoplex.dll"
}

Write-Host "Release lista: $out"
Get-ChildItem $out | Format-Table Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } } -AutoSize

if ($Zip) {
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host ("Zip: {0} ({1:N1} MB)" -f (Split-Path $zipPath -Leaf), ((Get-Item $zipPath).Length / 1MB))
}
