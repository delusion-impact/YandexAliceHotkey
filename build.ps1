# Сборка YandexAliceHotkey-v2.7.0.exe
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

$src = Join-Path $root 'YandexAliceHotkey.cs'
$out = Join-Path $root 'YandexAliceHotkey-v2.7.0.exe'

# csc читает исходник как ANSI без BOM - пересохраняем в UTF-8 с BOM (кириллица в строках)
$text = [System.IO.File]::ReadAllText($src, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($src, $text, [System.Text.UTF8Encoding]::new($true))

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$refs = @(
    (Join-Path $fw 'WPF\UIAutomationClient.dll'),
    (Join-Path $fw 'WPF\UIAutomationTypes.dll'),
    (Join-Path $fw 'WPF\WindowsBase.dll'),
    (Join-Path $fw 'System.Drawing.dll'),
    (Join-Path $fw 'System.Windows.Forms.dll'),
    (Join-Path $fw 'System.Runtime.WindowsRuntime.dll'),
    (Join-Path $fw 'System.Runtime.dll')
)
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
$winmdDir = Get-ChildItem -LiteralPath $kits -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object -Property Name -Descending | Select-Object -First 1
if (-not $winmdDir) { throw 'Windows.winmd not found; Windows OCR reference is required.' }
$winmd = Join-Path $winmdDir.FullName 'Windows.winmd'
if (-not (Test-Path -LiteralPath $winmd)) { throw "Windows OCR metadata is missing: $winmd" }
$refs += $winmd

$refArgs = $refs | ForEach-Object { "/r:$_" }
& $csc /nologo /target:winexe /optimize+ "/out:$out" $refArgs $src
if ($LASTEXITCODE -ne 0) { throw "csc failed with code $LASTEXITCODE" }

Write-Host "Built: $out"
Get-Item $out | Select-Object FullName, Length, LastWriteTime

