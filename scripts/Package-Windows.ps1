[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,
    [Parameter(Mandatory = $true)]
    [string]$CompilerPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../distributions')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-OrdinaryPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Ссылки и точки повторной обработки не допускаются: $($item.FullName)"
        }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
}

$source = (Get-Item -LiteralPath $SourceDirectory).FullName
$compiler = (Get-Item -LiteralPath $CompilerPath).FullName
Assert-OrdinaryPath $source
Assert-OrdinaryPath $compiler
$expected = @(
    'Assets/Flags/LICENSE.txt',
    'Assets/Lucide/LICENSE.txt',
    'LICENSE',
    'Licenses/dotnet/Desktop-LICENSE.txt',
    'Licenses/dotnet/Runtime-LICENSE.txt',
    'Licenses/dotnet/Runtime-THIRD-PARTY-NOTICES.txt',
    'THIRD_PARTY_NOTICES.txt',
    'boolka.exe'
) | Sort-Object
$allEntries = @(Get-ChildItem -LiteralPath $source -Force -Recurse)
foreach ($entry in $allEntries) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Ссылки не допускаются: $($entry.FullName)"
    }
}
$files = @($allEntries | Where-Object { -not $_.PSIsContainer })
$actual = @($files | ForEach-Object { [IO.Path]::GetRelativePath($source, $_.FullName).Replace('\', '/') } | Sort-Object)
if (@(Compare-Object -ReferenceObject $expected -DifferenceObject $actual).Count -ne 0) {
    throw 'Состав автономной сборки отличается от разрешённых восьми файлов.'
}
$exe = Join-Path $source 'boolka.exe'
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
$version = '{0}.{1}.{2}' -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $versionInfo.FileMajorPart -lt 0 -or $versionInfo.FilePrivatePart -ne 0) {
    throw 'Не удалось определить версию EXE.'
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = (Get-Item -LiteralPath $OutputDirectory).FullName
Assert-OrdinaryPath $output
$archiveName = "boolka-$version-windows-x64-standalone.zip"
$setupName = "boolka-$version-windows-x64-setup.exe"
$hashName = "boolka-$version-SHA256SUMS.txt"
foreach ($name in @($archiveName, $setupName, $hashName)) {
    if (Test-Path -LiteralPath (Join-Path $output $name)) { throw "Существующий выпуск не перезаписывается: $name" }
}

# Все файлы сначала создаются в отдельной папке; неполный выпуск не выглядит готовым.
$staging = Join-Path $output ('.pack-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    $iss = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../installer/boolka.iss')).Path
    & $compiler /Qp "/DSourceDir=$source" "/DOutputDir=$staging" "/DProductVersion=$version" "/DOutputName=$([IO.Path]::GetFileNameWithoutExtension($setupName))" $iss
    if ($LASTEXITCODE -ne 0) { throw "Ошибка компиляции установщика: $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath (Join-Path $staging $setupName))) { throw 'Компилятор не создал установщик.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $staging $archiveName
    [IO.Compression.ZipFile]::CreateFromDirectory($source, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entries = @($zip.Entries | Where-Object Name)
        $entryNames = @($entries.FullName | ForEach-Object { $_.Replace('\', '/') } | Sort-Object)
        if (@(Compare-Object -ReferenceObject $expected -DifferenceObject $entryNames).Count -ne 0) { throw 'Неверный состав ZIP.' }
        foreach ($entry in $entries) {
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            $original = Join-Path $source $entry.FullName
            if ($hash -ne (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash) { throw "ZIP отличается от исходника: $($entry.FullName)" }
        }
    } finally { $zip.Dispose() }
    $lines = foreach ($name in @($archiveName, $setupName)) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $staging $name) -Algorithm SHA256).Hash.ToLowerInvariant(), $name
    }
    $lines | Set-Content -LiteralPath (Join-Path $staging $hashName) -Encoding utf8
    foreach ($name in @($archiveName, $setupName, $hashName)) {
        Move-Item -LiteralPath (Join-Path $staging $name) -Destination (Join-Path $output $name)
    }
} finally {
    # Удаляем только собственную одноразовую staging-папку после проверки её границы.
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    if ([IO.Path]::GetDirectoryName($resolvedStaging) -ne $output -or [IO.Path]::GetFileName($resolvedStaging) -notmatch '^\.pack-[a-f0-9]{32}$') {
        throw 'Неверная граница временной папки упаковки.'
    }
    if (Test-Path -LiteralPath $resolvedStaging) { Remove-Item -LiteralPath $resolvedStaging -Recurse -Force }
}
Get-Item -LiteralPath (Join-Path $output $archiveName), (Join-Path $output $setupName), (Join-Path $output $hashName) | Select-Object Name, Length, FullName

