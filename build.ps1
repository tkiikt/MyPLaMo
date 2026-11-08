#requires -Version 7.0
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$true
Push-Location $PSScriptRoot
try {
    $folder='release/MyPLaMo'
    # Start from an empty payload so removed runtime files cannot survive an upgrade build.
    $payloadPath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $folder))
    $releaseRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'release'))+[IO.Path]::DirectorySeparatorChar
    if(!$payloadPath.StartsWith($releaseRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid release payload path'}
    if(Test-Path -LiteralPath $payloadPath){Remove-Item -LiteralPath $payloadPath -Recurse -Force}
    New-Item -ItemType Directory -Force build,engine,"$folder/engine","$folder/licenses" | Out-Null
    $archive='build/engine.zip'
    $tag='b12121'
    if(!(Test-Path $archive)){
        curl.exe -fLs --retry 3 -o $archive "https://github.com/ggml-org/llama.cpp/releases/download/$tag/llama-$tag-bin-win-vulkan-x64.zip"
    }
    if((Get-FileHash $archive).Hash -ne '73111d348dcbfa86050611c28b8bef523c849d80a6e5529e25b67f1b2055e918'){throw 'Unexpected engine archive hash'}
    tar -xf $archive -C engine
    Copy-Item engine/ggml*.dll,engine/libomp.dll,engine/llama.dll,engine/llama-common.dll,engine/mtmd.dll,engine/llama-server*,engine/LICENSE* "$folder/engine"
    curl.exe -fLs --retry 2 -o "$folder/engine/LICENSE-llama.cpp" "https://raw.githubusercontent.com/ggml-org/llama.cpp/$tag/LICENSE"
    # WinUI's native runtime and resource files travel with the portable folder/MSI.
    dotnet publish src -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o $folder
    $assets=Get-Content src/obj/project.assets.json -Raw |ConvertFrom-Json -AsHashtable
    foreach($dep in $assets.project.frameworks.Values.downloadDependencies | Where-Object {$_.name -match '^Microsoft\.(WindowsDesktop|NETCore|AspNetCore)\.App\.Runtime\.win-x64$'}){
        $ver=($dep.version.Trim('[',']') -split ',')[0].Trim()
        $lib=$dep.name+'/'+$ver
        $pkg=$lib.ToLowerInvariant()
        $base=$assets.packageFolders.Keys|ForEach-Object {Join-Path $_ $pkg}|Where-Object {Test-Path $_}|Select-Object -First 1
        Get-ChildItem $base -File |Where-Object {$_.Name -match '^LICENSE|^THIRD-PARTY-NOTICES'}|ForEach-Object {
            Copy-Item $_.FullName (Join-Path "$folder/licenses" (($lib -replace '/','-')+'-'+$_.Name))
        }
    }
    foreach($lib in $assets.libraries.Keys | Where-Object {$_ -match '^(Microsoft\.WindowsAppSDK(?:\.[^/]+)?|CommunityToolkit\.Mvvm)/'}){
        $base=$assets.packageFolders.Keys | ForEach-Object {Join-Path $_ $lib.ToLowerInvariant()} | Where-Object {Test-Path $_} | Select-Object -First 1
        Get-ChildItem -LiteralPath $base -File | Where-Object {$_.Name -match 'LICENSE|NOTICE'} | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path "$folder/licenses" (($lib -replace '/','-')+'-'+$_.Name))
        }
    }
    ./chrome/build.ps1
    Copy-Item README.md,LICENSE,LICENSE-MIT,MyPLaMo.png,MyPLaMo_Models.png $folder
    if(!(Test-Path build/tools/wix.exe)){dotnet tool install wix --version 5.0.2 --tool-path build/tools}
    build/tools/wix.exe extension add WixToolset.UI.wixext/5.0.2
    $mit=(Get-Content LICENSE-MIT) -join '\par '
    [IO.File]::WriteAllText("$PWD/build/license-mit.rtf",'{\rtf1\ansi '+$mit+'}')
    build/tools/wix.exe build setup.wxs -ext WixToolset.UI.wixext/5.0.2 -culture ja-JP -arch x64 -intermediateFolder build/wix -pdb build/setup.wixpdb -o release/MyPLaMo-Setup-x64.msi
    ./installer/Set-MsiElevation.ps1 -Path release/MyPLaMo-Setup-x64.msi
    Compress-Archive -Force -Path $folder -DestinationPath release/MyPLaMo-x64.zip
} finally {Pop-Location}



