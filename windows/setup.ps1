# Adds or removes PDF Viewer's Windows integration.
#
# Everything is written under HKCU and your own Start Menu: no administrator
# rights, nothing installed system-wide. The paths point at this folder, so it
# can live anywhere; after moving it, run "Add to Windows" again.

param(
  [switch]$Register,
  [switch]$Unregister
)

$ErrorActionPreference = 'Stop'

$app = $PSScriptRoot
if (-not $app) { $app = Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = Split-Path -Parent $app
$exe = Join-Path $root 'PDF Viewer.exe'
$progId = 'PDFViewer.Document'
$ext = '.pdf'
$verbKey = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\PDFViewer"
$appKey = 'HKCU:\Software\Classes\Applications\PDF Viewer.exe'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\PDF Viewer.lnk'
$command = '"{0}" "%1"' -f $exe
$iconRef = '"{0}",0' -f $exe

function Set-Key([string]$path, [string]$value) {
  if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
  if ($null -ne $value) { Set-ItemProperty -Path $path -Name '(Default)' -Value $value }
}

<#
  Make .pdf open with the viewer on double-click, but only when nothing else
  has claimed it. Windows protects a user's own choice with a hash, so a
  deliberate association elsewhere is left alone; Open with and the
  right-click entry still work.
#>
function Set-DefaultIfUnclaimed {
  $choice = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$ext\UserChoice"
  if (Test-Path $choice) {
    $owner = (Get-ItemProperty -Path $choice -ErrorAction SilentlyContinue).ProgId
    if ($owner -and $owner -ne $progId) { return $owner }
  }
  foreach ($scope in @("HKCU:\Software\Classes\$ext", "Registry::HKEY_CLASSES_ROOT\$ext")) {
    $owner = (Get-ItemProperty -Path $scope -Name '(Default)' -ErrorAction SilentlyContinue)."(Default)"
    # An uninstalled app can leave its name behind with nothing registered under it: that claims nothing.
    if ($owner -and $owner -ne $progId -and (Test-Path "Registry::HKEY_CLASSES_ROOT\$owner")) { return $owner }
  }
  Set-Key "HKCU:\Software\Classes\$ext" $progId
  return $null
}

function Invoke-ShellChanged {
  # Tell Explorer the associations moved, so icons refresh without signing out.
  $sig = @'
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
'@
  try {
    $t = Add-Type -MemberDefinition $sig -Name 'ShellNotify' -Namespace 'PDFViewer' -PassThru
    $t::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)
  } catch { }
}

# ---------------------------------------------------------------------------

if ($Register) {
  if (-not (Test-Path -LiteralPath $exe)) { throw "PDF Viewer.exe is missing from $root" }

  Write-Host ''
  Write-Host '  Adding PDF Viewer to Windows' -ForegroundColor Cyan
  Write-Host "  app folder: $root" -ForegroundColor DarkGray
  Write-Host ''

  # 0. Files unzipped from a mail or a share carry a "downloaded from the internet"
  #    mark that makes Windows warn on every start. Running this is the go-ahead.
  Get-ChildItem -LiteralPath $root -Recurse -File | Unblock-File

  # 1. The document type: name, icon and how to open it.
  $base = "HKCU:\Software\Classes\$progId"
  Set-Key $base 'PDF document'
  Set-ItemProperty -Path $base -Name 'FriendlyTypeName' -Value 'PDF document'
  Set-ItemProperty -Path $base -Name 'FriendlyAppName' -Value 'PDF Viewer'
  Set-Key "$base\DefaultIcon" $iconRef
  Set-Key "$base\shell\open\command" $command

  # 2. The application entry the Open with list reads its name and icon from.
  Set-Key $appKey $null
  Set-ItemProperty -Path $appKey -Name 'FriendlyAppName' -Value 'PDF Viewer'
  Set-Key "$appKey\DefaultIcon" $iconRef
  Set-Key "$appKey\shell\open\command" $command
  Set-Key "$appKey\SupportedTypes" $null
  New-ItemProperty -Path "$appKey\SupportedTypes" -Name $ext -Value '' -Force | Out-Null
  Write-Host '  [ok] registered the viewer' -ForegroundColor Green

  # 3. Offer it in the Open with list.
  $key = "HKCU:\Software\Classes\$ext\OpenWithProgids"
  if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
  New-ItemProperty -Path $key -Name $progId -Value ([byte[]]@()) -PropertyType None -Force | Out-Null

  # 4. An explicit right-click entry, there whatever owns the default.
  Set-Key $verbKey $null
  Set-ItemProperty -Path $verbKey -Name 'MUIVerb' -Value 'Open with PDF Viewer'
  Set-ItemProperty -Path $verbKey -Name 'Icon' -Value $iconRef
  Set-Key "$verbKey\command" $command
  Write-Host '  [ok] added "Open with PDF Viewer" to the right-click menu of PDF files' -ForegroundColor Green

  # 5. Double-click, if .pdf isn't already someone else's.
  $owner = Set-DefaultIfUnclaimed
  if ($owner) {
    Write-Host "  [--] PDF files stay with $owner on double-click." -ForegroundColor Yellow
    Write-Host '       To switch: right-click a PDF > Open with > Choose another app > PDF Viewer > Always.' -ForegroundColor Yellow
  } else {
    Write-Host '  [ok] PDF files now open with the viewer on double-click' -ForegroundColor Green
  }

  # 6. A Start Menu entry, pinnable to the taskbar.
  $ws = New-Object -ComObject WScript.Shell
  $lnk = $ws.CreateShortcut($shortcut)
  $lnk.TargetPath = $exe
  $lnk.WorkingDirectory = $root
  $lnk.IconLocation = '{0},0' -f $exe
  $lnk.Description = 'Borderless, minimal PDF viewer'
  $lnk.Save()
  Write-Host '  [ok] added to the Start Menu' -ForegroundColor Green

  Invoke-ShellChanged

  Write-Host ''
  Write-Host '  Done. If you move this folder, run this again from the new location.' -ForegroundColor DarkGray
  Write-Host ''
}

if ($Unregister) {
  Write-Host ''
  Write-Host '  Removing PDF Viewer from Windows' -ForegroundColor Cyan
  Write-Host ''

  Remove-Item -Path "HKCU:\Software\Classes\$progId" -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -Path $appKey -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -Path $verbKey -Recurse -Force -ErrorAction SilentlyContinue
  Remove-ItemProperty -Path "HKCU:\Software\Classes\$ext\OpenWithProgids" -Name $progId -Force -ErrorAction SilentlyContinue
  # Hand .pdf back only if we were the ones holding it.
  $key = "HKCU:\Software\Classes\$ext"
  $owner = (Get-ItemProperty -Path $key -Name '(Default)' -ErrorAction SilentlyContinue)."(Default)"
  if ($owner -eq $progId) { Set-ItemProperty -Path $key -Name '(Default)' -Value '' -ErrorAction SilentlyContinue }
  Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue

  Invoke-ShellChanged
  Write-Host '  [ok] removed. The app folder itself was left alone.' -ForegroundColor Green
  Write-Host ''
}

if (-not $Register -and -not $Unregister) {
  Write-Host 'Use -Register or -Unregister.'
}
