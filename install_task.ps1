# Установка/удаление задачи планировщика:
#   запуск YandexAliceHotkey.exe по событию создания процесса browser.exe
# (Security log, EventID 4688; для этого включается аудит создания процессов).
#
#   .\install_task.ps1          - установить
#   .\install_task.ps1 -Remove  - удалить задачу
#
# Скрипт сам поднимает себя через UAC, если запущен без прав администратора.

param([switch]$Remove)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $root) { $root = $PSScriptRoot }
$logPath = Join-Path $root 'task_install.log'
$exePath = Join-Path $root 'YandexAliceHotkey.exe'
$taskName = 'YandexAliceHotkey'
$browserExe = 'C:\Program Files\Yandex\YandexBrowser\Application\browser.exe'
$auditSubcat = '{0CCE922B-69AE-11D9-BED3-505054503030}'   # Process Creation

function Log($m) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $m"
    Add-Content -Path $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    # Перезапуск с повышением (появится запрос UAC - подтвердите).
    if (Test-Path $logPath) { Remove-Item $logPath -Force }
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Remove) { $argList += '-Remove' }
    try {
        Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait
    } catch {
        Write-Host "Запрос прав отклонён: $($_.Exception.Message)"
        exit 1
    }
    if (Test-Path $logPath) { Get-Content $logPath -Encoding UTF8 }
    exit 0
}

# ---------- дальше выполняется с правами администратора ----------
Log "=== install task '$taskName' (Remove=$Remove) ==="

if (-not (Test-Path $exePath)) {
    Log "ERROR: $exePath not found - сначала соберите проект (build.ps1)"
    exit 1
}

# 1. Аудит создания процессов (нужен для события 4688).
$cur = & auditpol /get /subcategory:$auditSubcat 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Log "ERROR: auditpol /get failed: $cur"
    exit 1
}
Log ("audit before: " + ($cur -split "`r?`n" | Where-Object { $_ -match 'Установлено|Enabled|Выключен|Включен' } | Select-Object -First 1).Trim())
& auditpol /set /subcategory:$auditSubcat /success:enable 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Log "ERROR: auditpol /set failed (нет прав?)"
    exit 1
}
$cur2 = & auditpol /get /subcategory:$auditSubcat 2>&1 | Out-String
Log ("audit after : " + ($cur2 -split "`r?`n" | Where-Object { $_ -match 'Установлено|Enabled|Выключен|Включен' } | Select-Object -First 1).Trim())

if ($Remove) {
    try {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Log "task '$taskName' removed"
    } catch { Log "task remove: $($_.Exception.Message)" }
    Log "=== done (audit policy left as is) ==="
    exit 0
}

if (-not (Test-Path $browserExe)) {
    Log "ERROR: browser not found at $browserExe"
    exit 1
}

# 2. XML задачи: событие Security/4688 с точным путём NewProcessName.
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$user = "$env:USERDOMAIN\$env:USERNAME"
$escaped = [System.Security.SecurityElement]::Escape($browserExe)
$subscription = "<QueryList><Query Id=`"0`" Path=`"Security`"><Select Path=`"Security`">" +
    "*[System[(EventID=4688)] and EventData[Data[@Name=`"NewProcessName`"]=`"$escaped`"]]" +
    "</Select></Query></QueryList>"

$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Starts YandexAliceHotkey.exe when Yandex Browser (browser.exe) process is created. Security/4688.</Description>
  </RegistrationInfo>
  <Triggers>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>$subscription</Subscription>
    </EventTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$sid</UserId>
      <DisplayName>$user</DisplayName>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$exePath</Command>
      <WorkingDirectory>$root</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@

# 3. Регистрация задачи.
try {
    Register-ScheduledTask -TaskName $taskName -Xml $xml -Force | Out-Null
    Log "task '$taskName' registered"
} catch {
    Log "ERROR: Register-ScheduledTask failed: $($_.Exception.Message)"
    exit 1
}

$t = Get-ScheduledTask -TaskName $taskName
Log ("task state: {0}; trigger: {1}" -f $t.State, ($t.Triggers | ForEach-Object { $_.CimClass.CimClassName } | Select-Object -First 1))
Log ("action   : {0}" -f ($t.Actions | ForEach-Object { $_.Execute }))
Log "=== done; запуск теперь: открыть любую вкладку/окно браузера ==="
