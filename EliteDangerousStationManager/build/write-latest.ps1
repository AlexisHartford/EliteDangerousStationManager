param(
  [string]$Version,                          # optional if -BumpFromLatest
  [Parameter(Mandatory=$true)] [string]$Out,               # path to latest.json
  [Parameter(Mandatory=$true)] [string]$InstallerPath,     # built EXE/MSI/ZIP
  [Parameter(Mandatory=$true)] [string]$InstallerUrl,      # public download URL
  [string]$NotesFile = "",
  [switch]$Mandatory,
  [switch]$BumpFromLatest                      # <- new
)

# Always mandatory if you want:
if (-not $Mandatory) { $Mandatory = $true }

# Ensure output directory exists
$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) {
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$notes = ""
if ($NotesFile -and (Test-Path $NotesFile)) {
  $lines = Get-Content $NotesFile | Where-Object { $_.Trim() -ne "" } | Select-Object -First 10
  $notes = ($lines -join "`n")
}

if (!(Test-Path $InstallerPath)) {
  throw "Installer not found at $InstallerPath"
}

# Determine version
function Bump-Version([string]$v) {
  if (-not $v) { return "0.0.0.1" }
  $parts = $v -split '\.'
  if ($parts.Count -eq 0) { return "0.0.0.1" }

  for ($i = $parts.Count - 1; $i -ge 0; $i--) {
    if ($parts[$i] -match '^\d+$') {
      $parts[$i] = ([int]$parts[$i] + 1).ToString()
      return ($parts -join '.')
    }
  }
  # no numeric segment? just append .1
  return ($v + ".1")
}

$newVersion = $null
if ($BumpFromLatest -and (Test-Path $Out)) {
  try {
    $cur = (Get-Content $Out -Raw | ConvertFrom-Json)
    $newVersion = Bump-Version $cur.version
    Write-Host "Bumping version from '$($cur.version)' -> '$newVersion'"
  } catch {
    Write-Warning "Could not read/parse $Out : $_"
  }
}

if (-not $newVersion) {
  if ($Version) {
    $newVersion = $Version
    Write-Host "Using provided version '$newVersion'"
  } else {
    $newVersion = "0.0.0.1"
    Write-Host "No version provided and no latest.json found; defaulting to '$newVersion'"
  }
}

$sha = (Get-FileHash -Algorithm SHA256 $InstallerPath).Hash

$obj = [pscustomobject]@{
  version      = $Version
  notes        = $notes
  mandatory    = $true
  installerUrl = $InstallerUrl
  sha256       = $sha
  restartOnly  = $true   # <— this makes the app just restart
}


($obj | ConvertTo-Json -Depth 5) | Out-File -Encoding utf8 -NoNewline $Out
Write-Host "latest.json written to $Out"
