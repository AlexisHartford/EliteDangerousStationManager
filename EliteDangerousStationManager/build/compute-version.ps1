param(
  [string]$LatestPath,              # e.g. X:\latest.json
  [string]$BaseVersion = "",        # optional e.g. "1.0.1" to lock major.minor.patch
  [Parameter(Mandatory=$true)]
  [string]$OutPath                  # where to write the computed version (plain text)
)

function Get-NextVersion {
  param([string]$current, [string]$base)

  # If we have a base like "1.0.1" and current is different major.minor.patch, reset to base.0
  if ($base) {
    if ($current -match '^(\d+)\.(\d+)\.(\d+)\.(\d+)$') {
      $prefix = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
      if ($prefix -ne $base) { return "$base.1" }
    } else {
      return "$base.1"
    }
  }

  if ($current -match '^(\d+)\.(\d+)\.(\d+)\.(\d+)$') {
    $maj=$Matches[1]; $min=$Matches[2]; $pat=$Matches[3]; $rev=[int]$Matches[4]+1
    return "$maj.$min.$pat.$rev"
  }

  if ($base) { return "$base.1" }
  return "1.0.0.1"
}

$cur = ""
if ($LatestPath -and (Test-Path $LatestPath)) {
  try {
    $json = Get-Content -Raw -Encoding UTF8 $LatestPath | ConvertFrom-Json
    $cur = "$($json.version)"
  } catch {
    $cur = ""
  }
}

$next = Get-NextVersion -current $cur -base $BaseVersion
Set-Content -LiteralPath $OutPath -Value $next -NoNewline -Encoding ASCII
Write-Host "Computed version: $next"
