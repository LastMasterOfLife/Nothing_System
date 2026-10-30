# Plugin "git" per Nothing Shell: lo stato di un repository come nella barra di stato di un file manager.
# Rosso quando ci sono commit da ricevere o conflitti. Solo ASCII: PowerShell 5.1 legge i .ps1 senza BOM come ANSI.
param([string]$Path = '.')

$status = @(git -C $Path status --porcelain=v2 --branch 2>$null)
if ($LASTEXITCODE) {
    @{ header = 'GIT'; value = '-'; caption = 'nessun repository git qui'; alert = $true } | ConvertTo-Json -Compress
    return
}
$branch = "$($status -match '^# branch.head ')" -replace '^# branch.head ', ''
$ahead = 0; $behind = 0
if ("$($status -match '^# branch.ab ')" -match '\+(\d+) -(\d+)') { $ahead = [int]$Matches[1]; $behind = [int]$Matches[2] }
$files = @($status -notmatch '^#')
$conflicts = @($files -match '^u ').Count

$caption = @("$($files.Count) modificati")
if ($ahead) { $caption += "$ahead da inviare" }
if ($behind) { $caption += "$behind da ricevere" }
if ($conflicts) { $caption += "$conflicts conflitti" }

@{
    header  = "GIT - $(Split-Path -Leaf (Resolve-Path $Path))".ToUpperInvariant()
    value   = $branch
    caption = $caption -join ' - '
    bar     = [math]::Min(1.0, $files.Count / 50)
    alert   = ($behind -gt 0 -or $conflicts -gt 0)
} | ConvertTo-Json -Compress
