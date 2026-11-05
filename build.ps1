#requires -Version 7.0
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$true
Push-Location $PSScriptRoot
try {
    $folder='release/MyPLaMo'
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
    dotnet publish src -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $folder
    $assets=Get-Content src/obj/project.assets.json -Raw |ConvertFrom-Json -AsHashtable
    foreach($dep in $assets.project.frameworks.Values.downloadDependencies | Where-Object {$_.name -match '^Microsoft\.(WindowsDesktop|NETCore)\.App\.Runtime\.win-x64$'}){
        $ver=($dep.version.Trim('[',']') -split ',')[0].Trim()
        $lib=$dep.name+'/'+$ver
        $pkg=$lib.ToLowerInvariant()
        $base=$assets.packageFolders.Keys|ForEach-Object {Join-Path $_ $pkg}|Where-Object {Test-Path $_}|Select-Object -First 1
        Get-ChildItem $base -File |Where-Object {$_.Name -match '^LICENSE|^THIRD-PARTY-NOTICES'}|ForEach-Object {
            Copy-Item $_.FullName (Join-Path "$folder/licenses" (($lib -replace '/','-')+'-'+$_.Name))
        }
    }
    Copy-Item README.md $folder
    if(!(Test-Path build/tools/wix.exe)){dotnet tool install wix --version 5.0.2 --tool-path build/tools}
    build/tools/wix.exe build setup.wxs -arch x64 -intermediateFolder build/wix -pdb build/setup.wixpdb -o release/MyPLaMo-Setup-x64.msi
    Compress-Archive -Force -Path $folder -DestinationPath release/MyPLaMo-x64.zip
} finally {Pop-Location}

