# Plugin "cestino" per Nothing Shell: elementi e spazio occupato dal cestino, rosso oltre la soglia (GB).
param([double]$MaxGb = 5)

$items = @((New-Object -ComObject Shell.Application).Namespace(10).Items())
$bytes = [double](($items | ForEach-Object { $_.Size }) | Measure-Object -Sum).Sum
$gb = $bytes / 1GB

@{
    header  = 'CESTINO'
    value   = $(if ($gb -ge 1) { "$([math]::Round($gb, 1)) GB" } else { "$([math]::Round($bytes / 1MB)) MB" })
    caption = "$($items.Count) elementi"
    bar     = [math]::Min(1.0, $gb / $MaxGb)
    alert   = ($gb -gt $MaxGb)
} | ConvertTo-Json -Compress
