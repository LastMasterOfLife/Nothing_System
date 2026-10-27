# Plugin "disco" per Nothing Shell: spazio occupato di un'unità, rosso sotto il 10% libero.
param([string]$Drive = 'C')

$d = Get-PSDrive -Name $Drive -PSProvider FileSystem
$total = $d.Used + $d.Free
$freeFraction = $d.Free / $total

@{
    header  = "DISCO ${Drive}:"
    value   = "$([math]::Round($d.Free / 1GB)) GB"
    caption = "liberi su $([math]::Round($total / 1GB)) GB"
    bar     = [math]::Round(1 - $freeFraction, 3)
    alert   = ($freeFraction -lt 0.10)
} | ConvertTo-Json -Compress
