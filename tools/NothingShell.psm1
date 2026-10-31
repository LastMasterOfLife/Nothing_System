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
    $phrase = $script:PendingPhrase
    $script:PendingPhrase = $null
    if (-not $last -or $last.Id -eq $script:LastHistoryId) { return }
    $script:LastHistoryId = $last.Id
    $code = if ($Succeeded) { 0 } elseif ($ExitCode -is [int] -and $ExitCode -ne 0) { $ExitCode } else { 1 }
    $folder = Get-NothingShellFolder

    # Il comando eseguito è quello con cui si impara: se è stato modificato prima di Invio, è una correzione.
    if ($phrase) {
        Send-NothingShellBrain @{ op = 'outcome'; input = $phrase; command = ($last.CommandLine -replace $script:BrainNote, ''); ok = ($code -eq 0) } 500 | Out-Null
    }

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

# --- Cervello dei comandi (docs/15-comandi-cervello.md) ---

$script:BrainPipe = if ($env:NOTHINGSHELL_BRAIN_PIPE) { $env:NOTHINGSHELL_BRAIN_PIPE } else { 'NothingShell.Brain' }
$script:BrainNote = '\s+# nothing:.*$'
$script:BrainWait = 35000
$script:PendingPhrase = $null
$script:BrainKeywords = 'if', 'else', 'elseif', 'for', 'foreach', 'while', 'do', 'switch', 'function', 'filter', 'try', 'catch',
    'finally', 'param', 'class', 'enum', 'return', 'exit', 'throw', 'break', 'continue', 'trap', 'using', 'begin', 'process', 'end', 'data'

# Una riga JSON verso la shell, una di risposta; $null se Nothing Shell non è aperta.
function Send-NothingShellBrain {
    param([Parameter(Mandatory)][hashtable]$Message, [int]$WaitMilliseconds = 2000)
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $script:BrainPipe, [System.IO.Pipes.PipeDirection]::InOut)
        try {
            $pipe.Connect(150)
            $utf8 = [System.Text.UTF8Encoding]::new($false)
            $writer = [System.IO.StreamWriter]::new($pipe, $utf8)
            $writer.AutoFlush = $true
            $writer.WriteLine(($Message | ConvertTo-Json -Compress))
            $read = [System.IO.StreamReader]::new($pipe, $utf8).ReadLineAsync()
            if ($read.Wait($WaitMilliseconds) -and $read.Result) { return $read.Result | ConvertFrom-Json }
        }
        finally {
            $pipe.Dispose()
        }
    }
    catch {
        # Nothing Shell non è aperta: PowerShell funziona come sempre
    }
    return $null
}

# Solo ciò che PowerShell non saprebbe eseguire passa dal cervello: un comando vero parte com'è, senza attese.
function Test-NothingShellPhrase {
    param([string]$Line)
    if ($Line -match '[\r\n|;&$<>{}=]') { return $false }
    if ($Line -notmatch "^\s*([\p{L}]+(?:['’][\p{L}]+)?)(?:\s|$)") { return $false }
    $word = $Matches[1]
    if ($script:BrainKeywords -contains $word.ToLowerInvariant()) { return $false }
    return -not (Get-Command -Name $word -ErrorAction Ignore)
}

function Format-NothingShellNote {
    param($Reply)
    $origin = switch ($Reply.source) { 'model' { 'modello' } 'similar' { 'simile' } default { 'scorciatoia' } }
    $state = if ($Reply.runNow) { 'eseguo' } elseif ($Reply.risk -eq 'dangerous') { 'PERICOLOSO, Invio per eseguire' } else { 'Invio per eseguire' }
    "  # nothing: $origin $([int][math]::Ceiling($Reply.ms)) ms, $state"
}

# Il tasto Invio del terminale.
function Invoke-NothingShellEnter {
    $line = $null
    $cursor = $null
    [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)

    # Seconda pressione su una proposta, comando normale o frase forzata con il punto interrogativo
    $forced = $line -match '^\s*\?\s*(\S.*)$'
    $phrase = if ($forced) { $Matches[1].Trim() } else { $line.Trim() }
    if ($script:PendingPhrase -or $phrase.Length -eq 0 -or (-not $forced -and -not (Test-NothingShellPhrase $line))) {
        [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
        return
    }

    $working = "$line  # nothing: penso..."
    [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $line.Length, $working)
    $reply = Send-NothingShellBrain @{ op = 'decide'; input = $phrase; forced = [bool]$forced; cwd = (Get-NothingShellFolder) } $script:BrainWait
    $valid = $false
    if ($reply -and $reply.command) {
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($reply.command, [ref]$null, [ref]$errors)
        # Il modello a volte risponde con una frase: si propone solo un comando che esiste davvero.
        $names = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ }
        $valid = $errors.Count -eq 0 -and $names -and -not ($names | Where-Object { -not (Get-Command -Name $_ -ErrorAction Ignore) })
    }

    if (-not $valid) {
        if ($forced) {
            $why = if ($reply -and $reply.note) { $reply.note } elseif (-not $reply) { 'Nothing Shell non è aperta' } else { 'nessuna decisione' }
            [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $working.Length, "# nothing: $why")
            return
        }
        # Nessuna decisione: la riga va a PowerShell, che dà il suo errore come sempre.
        [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $working.Length, $line)
        [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
        return
    }

    $script:PendingPhrase = $phrase
    [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $working.Length, $reply.command + (Format-NothingShellNote $reply))
    if ($reply.runNow) { [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine() }
}

<#
.SYNOPSIS
Le frasi che PowerShell non capisce passano dal cervello di Nothing Shell; "? frase" lo chiede sempre.
#>
function Enable-NothingShellBrain {
    Enable-NothingShellPrompt
    if (-not (Get-Module PSReadLine)) {
        Write-Warning 'Nothing Shell: PSReadLine non è attivo, le frasi non vengono riconosciute.'
        return
    }
    $module = $ExecutionContext.SessionState.Module
    $handler = { param($key, $arg) & $module { Invoke-NothingShellEnter } }.GetNewClosure()
    Set-PSReadLineKeyHandler -Key Enter -BriefDescription NothingShellEnter -Description 'Esegue la riga; una frase passa dal cervello di Nothing Shell' -ScriptBlock $handler
}

function Disable-NothingShellBrain {
    Set-PSReadLineKeyHandler -Key Enter -Function AcceptLine
}

<#
.SYNOPSIS
Esegue un comando già confermato (dal launcher) e impara dall'esito.
#>
function Invoke-NothingShellProposal {
    param([string]$Phrase, [Parameter(Mandatory)][string]$Command)
    $global:LASTEXITCODE = 0
    $failed = $false
    try {
        Invoke-Expression $Command | Out-Host
        if (-not $?) { $failed = $true }
    }
    catch {
        $failed = $true
        Write-Error $_
    }
    if ($Phrase) {
        Send-NothingShellBrain @{ op = 'outcome'; input = $Phrase; command = $Command; ok = (-not $failed -and $global:LASTEXITCODE -eq 0) } 500 | Out-Null
    }
}

<#
.SYNOPSIS
Insegna una scorciatoia: ricorda "stato del server" "ssh pi@raspberry uptime"
#>
function Set-NothingShellShortcut {
    param([Parameter(Mandatory)][string]$Phrase, [Parameter(Mandatory)][string]$Command)
    $reply = Send-NothingShellBrain @{ op = 'remember'; input = $Phrase; command = $Command }
    if (-not $reply) { Write-Warning 'Nothing Shell non è aperta.' } elseif ($reply.note) { Write-Warning $reply.note }
}

<#
.SYNOPSIS
Dimentica una scorciatoia: dimentica "stato del server"
#>
function Remove-NothingShellShortcut {
    param([Parameter(Mandatory)][string]$Phrase)
    $reply = Send-NothingShellBrain @{ op = 'forget'; input = $Phrase }
    if (-not $reply) { Write-Warning 'Nothing Shell non è aperta.' } elseif ($reply.note) { Write-Warning $reply.note }
}

Set-Alias -Name ns -Value Invoke-NothingShellTask
Set-Alias -Name ricorda -Value Set-NothingShellShortcut
Set-Alias -Name dimentica -Value Remove-NothingShellShortcut
Export-ModuleMember -Function Invoke-NothingShellTask, Enable-NothingShellPrompt, Disable-NothingShellPrompt, Send-NothingShellMessage, Enable-NothingShellBrain, Disable-NothingShellBrain, Invoke-NothingShellProposal, Set-NothingShellShortcut, Remove-NothingShellShortcut -Alias ns, ricorda, dimentica
