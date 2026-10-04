# Genera una release limpia de Echoplex en app\Echoplex-<versión>\ (con -Zip, además un .zip de la carpeta).
# La carpeta lleva Echoplex.exe con sus dependencias al lado (sin empaquetar en un único archivo):
#   Echoplex.exe, Echoplex.dll, CommunityToolkit.Mvvm.dll, NAudio.*.dll, TagLibSharp.dll, *.deps.json, *.runtimeconfig.json
# Las DLL de terceros van recortadas con ILLink a lo que usa Echoplex (trim.xml: lo que hay que conservar).
#
#   .\publish.ps1                          -> ligera: usa el .NET 8 Desktop Runtime instalado en el sistema
#   .\publish.ps1 -Portable                -> autocontenida: funciona en cualquier PC sin instalar .NET (mucho más grande)
#   .\publish.ps1 -Version 2.3.0           -> fuerza la versión (por defecto, la <Version> de Echoplex.csproj)
#   .\publish.ps1 -Zip                     -> además, Echoplex-<versión>-win-x64.zip con toda la carpeta
#   .\publish.ps1 -NoTrim                  -> sin recortar las DLL de terceros (para descartar el recorte si algo falla)
#
# Firma (opcional; sin ella Windows SmartScreen avisa en otros PCs):
#   .\publish.ps1 -CertThumbprint <SHA1>   -> certificado de firma de código instalado (token USB, Certum, etc.)
#   .\publish.ps1 -PfxPath cert.pfx -PfxPassword ...   -> certificado en archivo (sirve para probar la firma)
#   -TimestampUrl                          -> servidor de sellado de tiempo (la firma sigue valiendo cuando caduque el certificado)
param(
    [string]$Version,
    [switch]$Portable,
    [switch]$Zip,
    [switch]$NoTrim,
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

# Recorte: de NAudio, TagLib# y CommunityToolkit.Mvvm solo queda lo que usa Echoplex (de ~1 MB a ~0,4 MB).
# Echoplex.dll y .NET no se tocan. ILLink llega con dotnet restore (PackageDownload en Echoplex.csproj).
if (-not $NoTrim) {
    $trimmed = 'CommunityToolkit.Mvvm', 'NAudio.Core', 'NAudio.Wasapi', 'TagLibSharp'
    $illinkVersion = ([xml](Get-Content $proj)).Project.ItemGroup.PackageDownload |
        Where-Object { $_.Include -eq 'Microsoft.NET.ILLink.Tasks' } | ForEach-Object { $_.Version.Trim('[', ']') }
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $illink = Join-Path $packages "microsoft.net.illink.tasks\$illinkVersion\tools\net8.0\illink.dll"
    if (-not (Test-Path $illink)) { throw "No se encuentra ILLink en $illink" }

    # dónde está .NET: la portable lo lleva dentro; si no, el instalado. WindowsDesktop va primero porque
    # Microsoft.NETCore.App trae un WindowsBase.dll de compatibilidad que no es el de WPF.
    $search = @('-d', $out)
    if (-not $Portable) {
        $runtimes = dotnet --list-runtimes
        foreach ($fw in 'Microsoft.WindowsDesktop.App', 'Microsoft.NETCore.App') {
            $dir = $runtimes | ForEach-Object {
                if ($_ -match "^$([regex]::Escape($fw)) (8\.0\.\d+) \[(.+)\]$") { [pscustomobject]@{ V = [version]$Matches[1]; Dir = Join-Path $Matches[2] $Matches[1] } }
            } | Sort-Object V | Select-Object -Last 1 -ExpandProperty Dir
            if (-not $dir) { throw "Para recortar hace falta $fw 8.0 instalado" }
            $search += '-d', $dir
        }
    }

    $trimOut = Join-Path ([IO.Path]::GetTempPath()) "echoplex-recorte-$PID"
    $illinkArgs = @('-a', (Join-Path $out 'Echoplex.dll'), 'all', '-x', (Join-Path $PSScriptRoot 'trim.xml')) + $search + @(
        '--trim-mode', 'skip', '--action', 'skip', '--action', 'copy', 'Echoplex',
        '--skip-unresolved', 'true', '--deterministic', '-out', $trimOut)
    foreach ($a in $trimmed) { $illinkArgs += '--action', 'link', $a }
    $log = dotnet $illink @illinkArgs # ILLink lo escribe todo (avisos y errores) en la salida normal
    if ($LASTEXITCODE -ne 0) { $log | Write-Host; throw "El recorte con ILLink falló" }
    foreach ($a in $trimmed) { Copy-Item (Join-Path $trimOut "$a.dll") $out -Force }
    Remove-Item $trimOut -Recurse -Force
    Write-Host "Recortadas: $($trimmed -join ', ')"
}

# Firma: solo lo nuestro (Echoplex.exe y Echoplex.dll); las DLL de terceros no se firman.
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
    # con 7-Zip si está (viene en los equipos de GitHub Actions): un zip normal, pero ~5 % más pequeño que Compress-Archive
    $sevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
    if (Test-Path $sevenZip) {
        & $sevenZip a -tzip -mm=Deflate -mx=9 $zipPath (Join-Path $out '*') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7-Zip falló" }
    } else {
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zipPath -CompressionLevel Optimal
    }
    Write-Host ("Zip: {0} ({1:N1} MB)" -f (Split-Path $zipPath -Leaf), ((Get-Item $zipPath).Length / 1MB))
}
