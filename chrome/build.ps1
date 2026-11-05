$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
$release = Join-Path $root 'release'
$dest = Join-Path $release 'MyPLaMo-Web'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$PSScriptRoot/extension" $dest -Recurse -Force
Copy-Item "$root/LICENSE","$root/LICENSE-MIT","$PSScriptRoot/README.md" $dest -Force
$package = @("$dest/README.md","$dest/LICENSE","$dest/LICENSE-MIT")
Compress-Archive -Path (@("$PSScriptRoot/extension") + $package) -DestinationPath "$release/MyPLaMo-Web.zip" -Force
