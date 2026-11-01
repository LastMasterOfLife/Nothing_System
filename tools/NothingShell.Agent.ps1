# Nothing Shell: esegue un comando in sola lettura per l'assistente del notch (docs/15-comandi-cervello.md).
#
#   powershell -NoProfile -NonInteractive -File NothingShell.Agent.ps1 <comando in Base64, UTF-16>
#
# Quello che propone il modello può partire senza conferma solo se passa da qui. Tre barriere, una dentro l'altra:
#   1. l'albero sintattico: solo comandi di un elenco chiuso, scritti per esteso, senza metodi né assegnazioni;
#   2. una sessione a parte in cui tutto il resto è nascosto e non ci sono programmi fuori elenco;
#   3. il linguaggio limitato di PowerShell (niente .NET).
# La prima è indispensabile: un blocco { } dato a Where-Object vede anche i comandi nascosti.
# Scrive una riga JSON: status = ok | failed | refused (fuori elenco: decide l'utente) | rewrite (si può riscrivere in
# sola lettura: lo fa il modello), output, error, reason.

param([Parameter(Mandatory)][string]$Encoded)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$Cmdlets = @(
    'Get-Process', 'Get-Service', 'Get-ChildItem', 'Get-Item', 'Get-ItemProperty', 'Get-Content', 'Get-Location', 'Get-Date',
    'Get-PSDrive', 'Get-WmiObject', 'Get-NetConnectionProfile', 'Get-NetIPConfiguration', 'Get-Volume', 'Get-Disk', 'Get-Partition', 'Get-PhysicalDisk', 'Get-CimInstance', 'Get-ComputerInfo', 'Get-HotFix',
    'Get-TimeZone', 'Get-Culture', 'Get-NetIPAddress', 'Get-NetAdapter', 'Get-NetTCPConnection', 'Get-NetUDPEndpoint', 'Get-NetRoute',
    'Get-DnsClientServerAddress', 'Get-WinEvent', 'Get-ScheduledTask', 'Get-Printer', 'Get-LocalUser', 'Get-Command', 'Get-FileHash',
    'Get-PnpDevice', 'Get-NetFirewallProfile', 'Get-MpComputerStatus', 'Get-SmbShare', 'Get-Host',
    'Get-Acl', 'Get-Member', 'Get-Unique', 'Test-Path', 'Join-Path', 'Split-Path', 'Resolve-Path', 'Select-String', 'Where-Object',
    'Select-Object', 'Sort-Object', 'Measure-Object', 'Group-Object', 'Compare-Object', 'ForEach-Object', 'Format-Table', 'Format-List',
    'Format-Wide', 'Out-String', 'ConvertTo-Json', 'ConvertTo-Csv'
)
$Aliases = @('ls', 'dir', 'gci', 'gc', 'cat', 'type', 'pwd', 'gl', 'ps', 'gps', 'gsv', 'gcm', 'gi', 'gp', 'where', '?', 'select', 'sort', 'measure', 'group', 'foreach', '%', 'ft', 'fl', 'fw', 'sls', 'rvpa')
$Programs = @('git', 'ipconfig', 'whoami', 'hostname', 'netstat', 'systeminfo', 'tasklist')

# Moduli lenti da caricare (fino a 800 ms): solo se il comando li usa.
$Modules = @{
    'Get-NetConnectionProfile' = 'NetConnection'; 'Get-NetIPConfiguration' = 'NetTCPIP'; 'Get-NetIPAddress' = 'NetTCPIP'; 'Get-NetTCPConnection' = 'NetTCPIP'; 'Get-NetUDPEndpoint' = 'NetTCPIP'; 'Get-NetRoute' = 'NetTCPIP'
    'Get-NetAdapter' = 'NetAdapter'; 'Get-Volume' = 'Storage'; 'Get-Disk' = 'Storage'; 'Get-Partition' = 'Storage'; 'Get-PhysicalDisk' = 'Storage'
    'Get-DnsClientServerAddress' = 'DnsClient'; 'Get-ScheduledTask' = 'ScheduledTasks'; 'Get-Printer' = 'PrintManagement'
    'Get-LocalUser' = 'Microsoft.PowerShell.LocalAccounts'; 'Get-PnpDevice' = 'PnpDevice'; 'Get-NetFirewallProfile' = 'NetSecurity'
    'Get-MpComputerStatus' = 'ConfigDefender'; 'Get-SmbShare' = 'SmbShare'; 'Get-WinEvent' = 'Microsoft.PowerShell.Diagnostics'
}

# Niente rete né sessioni verso altri computer, niente scrittura attraverso i parametri comuni.
$ForbiddenParameters = @('ComputerName', 'CimSession', 'Session', 'Credential', 'Wait', 'OutVariable', 'ErrorVariable', 'WarningVariable', 'InformationVariable', 'PipelineVariable')
$ForbiddenAliases = @('cn', 'ov', 'ev', 'wv', 'iv', 'pv')
$Variables = @('_', 'PSItem', 'true', 'false', 'null', 'HOME', 'PID', 'PWD', 'env:USERPROFILE', 'env:TEMP', 'env:TMP', 'env:APPDATA', 'env:LOCALAPPDATA', 'env:SystemRoot', 'env:windir', 'env:ProgramFiles', 'env:ProgramData', 'env:COMPUTERNAME', 'env:USERNAME')
$Sensitive = '(^|[\\/])\.(ssh|aws|gnupg|azure|kube|docker)([\\/]|$)|(^|[\\/])\.env(\.|$)|id_(rsa|ed25519|ecdsa|dsa)|\.(pem|pfx|p12|key|kdbx)$|\.git-credentials|(^|[\\/])credentials|Login Data|(^|[\\/])Cookies$|^env:'
$GitReaders = @('status', 'log', 'diff', 'show', 'blame', 'shortlog', 'describe', 'rev-parse', 'branch', 'tag', 'remote')

function Write-Result {
    param([string]$Status, [string]$Output = '', [string]$Problem = '', [string]$Reason = '')
    [Console]::Out.WriteLine((@{ status = $Status; output = $Output; error = $Problem; reason = $Reason } | ConvertTo-Json -Compress))
}

function Get-ProgramProblem {
    param($Name, $Words)
    switch ($Name) {
        'git' {
            $rest = @($Words)
            if ($rest.Count -ge 2 -and $rest[0] -ceq '-C') { $rest = @($rest | Select-Object -Skip 2) }
            if ($rest.Count -eq 0 -or $GitReaders -notcontains $rest[0]) { return 'git solo in lettura' }
            if ($rest[0] -in 'branch', 'tag', 'remote' -and @($rest | Select-Object -Skip 1 | Where-Object { $_ -notmatch '^(-a|-r|-l|-v|-vv|--list|--all|--remotes|--show-current)$' }).Count -gt 0) { return 'git solo in lettura' }
            if (@($rest | Where-Object { $_ -match '^(--output|--ext-diff|--textconv|--exec|--upload-pack|-c$)' }).Count -gt 0) { return 'opzione di git non permessa' }
        }
        'ipconfig' { if (@($Words | Where-Object { $_ -ne '/all' }).Count -gt 0) { return 'ipconfig solo in lettura' } }
        'netstat' { if (@($Words | Where-Object { $_ -notmatch '^-[abefnoqrstxy]+$' }).Count -gt 0) { return 'netstat: opzione non permessa' } }
        default { if (@($Words | Where-Object { $_ -match '^[/-](s|u|p)$' }).Count -gt 0) { return "$Name verso un altro computer" } }
    }
    return $null
}

# Null se il comando è ammesso, altrimenti il motivo.
function Get-CommandProblem {
    param([string]$Command)
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Command, [ref]$null, [ref]$errors)
    if ($errors.Count -gt 0) { return "non si capisce: $($errors[0].Message)" }

    foreach ($node in $ast.FindAll({ $true }, $true)) {
        switch ($node.GetType().Name) {
            'AssignmentStatementAst' { return 'rewrite:assegnazione' }
            'FileRedirectionAst' { return 'scrive su un file' }
            'FunctionDefinitionAst' { return 'definisce una funzione' }
            'TypeDefinitionAst' { return 'definisce un tipo' }
            'UsingStatementAst' { return 'carica codice' }
            'TrapStatementAst' { return 'costrutto non permesso' }
            'WhileStatementAst' { return 'rewrite:ciclo' }
            'DoWhileStatementAst' { return 'rewrite:ciclo' }
            'DoUntilStatementAst' { return 'rewrite:ciclo' }
            'ForStatementAst' { return 'rewrite:ciclo' }
            'ForEachStatementAst' { return 'rewrite:ciclo' }
            'SubExpressionAst' { return 'rewrite:sottoespressione' }
            'InvokeMemberExpressionAst' { return 'rewrite:niente metodi: per i GB basta dividere, $_.Free/1GB' }
            'BinaryExpressionAst' {
                # Con stringhe composte si potrebbe costruire un percorso che i controlli qui sotto non vedono.
                if ($node.Operator -in 'Plus', 'Format', 'Join', 'Ireplace', 'Creplace') { return 'rewrite:niente -f, + o -join: lascia il numero cosi com''e' }
            }
            'VariableExpressionAst' {
                if ($Variables -notcontains $node.VariablePath.UserPath) { return "variabile `$$($node.VariablePath.UserPath)" }
            }
            'StringConstantExpressionAst' {
                if ($node.Value -match '^(\\\\|//)|://') { return 'percorso di rete' }
                if ($node.Value -match $Sensitive) { return 'file con credenziali' }
            }
            'ExpandableStringExpressionAst' {
                if ($node.Value -match '^(\\\\|//)|://') { return 'percorso di rete' }
                if ($node.Value -match $Sensitive) { return 'file con credenziali' }
            }
            'CommandAst' {
                $name = $node.GetCommandName()
                if (-not $name) { return 'nome del comando calcolato' }
                $words = @($node.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.Extent.Text.Trim("'", '"') })
                $program = $name -replace '\.exe$', ''
                if ($Programs -contains $program) {
                    $problem = Get-ProgramProblem $program $words
                    if ($problem) { return $problem }
                    continue
                }
                if ($Cmdlets -notcontains $name -and $Aliases -notcontains $name) { return "$name non è in elenco" }
                # PowerShell accetta i nomi abbreviati (-Comp): conta anche ogni inizio di un nome vietato.
                foreach ($parameter in $node.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] }) {
                    $given = $parameter.ParameterName
                    if ($ForbiddenAliases -contains $given -or @($ForbiddenParameters | Where-Object { $_.StartsWith($given, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
                        return "parametro -$given"
                    }
                }
                if ($name -in 'ForEach-Object', 'foreach', '%' -and @($node.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.ScriptBlockExpressionAst] }).Count -eq 0) {
                    return 'rewrite:ForEach-Object solo con un blocco'
                }
            }
        }
    }
    return $null
}

function Invoke-Restricted {
    param([string]$Command)
    $state = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault2()
    $names = [System.Management.Automation.Language.Parser]::ParseInput($Command, [ref]$null, [ref]$null).FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() }
    $needed = @($names | ForEach-Object { $Modules[$_] } | Where-Object { $_ } | Select-Object -Unique)
    $state.ImportPSModule(@('Microsoft.PowerShell.Management', 'Microsoft.PowerShell.Utility', 'CimCmdlets') + $needed)
    $runspace = [runspacefactory]::CreateRunspace($state)
    $runspace.Open()
    try {
        foreach ($item in $runspace.SessionStateProxy.InvokeCommand.GetCommands('*', 'Alias,Function,Filter,Cmdlet', $true)) {
            if ($Cmdlets -notcontains $item.Name -and $Aliases -notcontains $item.Name) { $item.Visibility = 'Private' }
        }
        $runspace.SessionStateProxy.Applications.Clear()
        $runspace.SessionStateProxy.Scripts.Clear()
        foreach ($program in @($names | ForEach-Object { $_ -replace '\.exe$', '' } | Where-Object { $Programs -contains $_ } | Select-Object -Unique)) {
            $found = Get-Command -Name $program -CommandType Application -ErrorAction Ignore | Select-Object -First 1
            if ($found) { $runspace.SessionStateProxy.Applications.Add($found.Source) }
        }
        $runspace.SessionStateProxy.LanguageMode = 'ConstrainedLanguage'

        $shell = [powershell]::Create()
        $shell.Runspace = $runspace
        [void]$shell.AddScript($Command).AddCommand('Out-String').AddParameter('Width', 160)
        $text = ($shell.Invoke() | ForEach-Object { "$_" }) -join ''
        $problems = ($shell.Streams.Error | ForEach-Object { "$_" }) -join "`n"
        Write-Result $(if ($shell.HadErrors -and -not $text.Trim()) { 'failed' } else { 'ok' }) $text.TrimEnd() $problems
    }
    finally {
        $runspace.Close()
    }
}

$env:GIT_TERMINAL_PROMPT = '0'
$env:GIT_PAGER = 'cat'
$env:GIT_CONFIG_COUNT = '1'
$env:GIT_CONFIG_KEY_0 = 'core.fsmonitor'
$env:GIT_CONFIG_VALUE_0 = 'false'

try {
    $text = [System.Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($Encoded))
    $reason = Get-CommandProblem $text
    if ($reason) {
        if ($reason.StartsWith('rewrite:')) { Write-Result 'rewrite' -Reason $reason.Substring(8) } else { Write-Result 'refused' -Reason $reason }
    }
    else {
        Invoke-Restricted $text
    }
}
catch {
    Write-Result 'failed' -Problem "$_"
}
