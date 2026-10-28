# Nothing Shell: comandi, build e git push nel notch (docs/12-sviluppo.md).
#
#   Import-Module '<cartella di NothingShell.exe>\tools\NothingShell.psm1'
#   Enable-NothingShellPrompt          # facoltativo: comandi lunghi e git push anche senza ns
#
#   ns dotnet build                    # inizio e fine nel notch, primo errore dal log
#
# Funziona con Windows PowerShell 5.1 e PowerShell 7. Se Nothing Shell non è aperta non succede nulla.

$script:PipeName = 'NothingShell.Tasks'
$script:LogFolder = Join-Path ([System.IO.Path]::GetTempPath()) 'nothing-shell'
$script:PromptMinimum = 30
$script:LastHistoryId = $null
$script:OriginalPrompt = $null

# Comandi interattivi: durano tanto ma non sono lavoro da segnalare.
$script:Interactive = '^\s*(ssh|vim|nvim|vi|nano|less|more|man|top|htop|btop|lazygit|tig|k9s|ns|Invoke-NothingShellTask)\b'

function Send-NothingShellMessage {
    param([Parameter(Mandatory)][hashtable]$Message)
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $script:PipeName, [System.IO.Pipes.PipeDirection]::Out)
        try {
            $pipe.Connect(200)
            $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes(($Message | ConvertTo-Json -Compress) + "`n")
            $pipe.Write($bytes, 0, $bytes.Length)
            $pipe.Flush()
        }
        finally {
            $pipe.Dispose()
        }
    }
    catch {
        # Nothing Shell non è aperta
    }
}

function Get-NothingShellFolder {
    (Get-Location).ProviderPath
}

<#
.SYNOPSIS
Esegue un comando e ne mostra inizio, tempo ed esito nel notch di Nothing Shell.
.EXAMPLE
ns dotnet build
.EXAMPLE
ns npm run test -- --watch=false
#>
function Invoke-NothingShellTask {
    if ($args.Count -eq 0) {
        Write-Host 'Uso: ns <comando> [argomenti]'
        return
    }
    $command = $args[0]
    $rest = @($args | Select-Object -Skip 1)
    $id = [guid]::NewGuid().ToString('n')
    $title = ($args | ForEach-Object { "$_" }) -join ' '
    $folder = Get-NothingShellFolder

    New-Item -ItemType Directory -Force -Path $script:LogFolder | Out-Null
    Get-ChildItem -Path $script:LogFolder -Filter 'task-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -Skip 19 |
        Remove-Item -ErrorAction SilentlyContinue
    $log = Join-Path $script:LogFolder "task-$id.log"

    Send-NothingShellMessage @{ event = 'start'; id = $id; title = $title; cwd = $folder; pid = $PID }
    $exitCode = 130 # interrotto con Ctrl+C, se non si arriva alla fine
    try {
        $global:LASTEXITCODE = 0
        $failed = $false
        # stderr nel log come testo (in PowerShell 5.1 diventerebbe un ErrorRecord)
        & $command @rest 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { "$_" } else { $_ }
        } | Tee-Object -FilePath $log
        if (-not $?) { $failed = $true }
        $exitCode = if ($global:LASTEXITCODE -ne 0) { $global:LASTEXITCODE } elseif ($failed) { 1 } else { 0 }
    }
    catch {
        Write-Error $_
        $exitCode = 1
    }
    finally {
        Send-NothingShellMessage @{ event = 'end'; id = $id; exitCode = $exitCode; log = $log }
        if ($exitCode -eq 0 -and $title -match '^\s*git\s+push\b') {
            Send-NothingShellMessage @{ event = 'push'; cwd = $folder }
        }
    }
}

function Watch-NothingShellLastCommand {
    param([bool]$Succeeded, $ExitCode)
    $last = Get-History -Count 1
    if (-not $last -or $last.Id -eq $script:LastHistoryId) { return }
    $script:LastHistoryId = $last.Id
    $code = if ($Succeeded) { 0 } elseif ($ExitCode -is [int] -and $ExitCode -ne 0) { $ExitCode } else { 1 }
    $folder = Get-NothingShellFolder

    if ($code -eq 0 -and $last.CommandLine -match '^\s*git\s+push\b') {
        Send-NothingShellMessage @{ event = 'push'; cwd = $folder }
    }
    $seconds = ($last.EndExecutionTime - $last.StartExecutionTime).TotalSeconds
    if ($seconds -lt $script:PromptMinimum -or $last.CommandLine -match $script:Interactive) { return }
    Send-NothingShellMessage @{
        event = 'done'; title = $last.CommandLine; seconds = [math]::Round($seconds, 1)
        exitCode = $code; cwd = $folder; pid = $PID; quiet = $true
    }
}

<#
.SYNOPSIS
Dopo ogni comando lungo (30 s o più) e dopo ogni git push riuscito avvisa Nothing Shell, senza ns.
Il prompt che c'era (anche oh-my-posh o starship) resta lo stesso.
#>
function Enable-NothingShellPrompt {
    param([int]$MinimumSeconds = 30)
    $script:PromptMinimum = $MinimumSeconds
    if ($null -ne $script:OriginalPrompt) { return } # già attivo
    $script:OriginalPrompt = (Get-Item function:prompt).ScriptBlock
    $script:LastHistoryId = (Get-History -Count 1).Id
    $module = $ExecutionContext.SessionState.Module
    $wrapper = {
        $succeeded = $?
        $exitCode = $global:LASTEXITCODE
        & $module { param($s, $e) Watch-NothingShellLastCommand $s $e } $succeeded $exitCode
        & $module { & $script:OriginalPrompt }
    }.GetNewClosure()
    Set-Item -Path function:global:prompt -Value $wrapper
}

function Disable-NothingShellPrompt {
    if ($null -eq $script:OriginalPrompt) { return }
    Set-Item -Path function:global:prompt -Value $script:OriginalPrompt
    $script:OriginalPrompt = $null
}

Set-Alias -Name ns -Value Invoke-NothingShellTask
Export-ModuleMember -Function Invoke-NothingShellTask, Enable-NothingShellPrompt, Disable-NothingShellPrompt, Send-NothingShellMessage -Alias ns
