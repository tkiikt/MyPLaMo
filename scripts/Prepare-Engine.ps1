#requires -Version 7.0
param([string]$Destination = (Join-Path $PSScriptRoot '../engine'))
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destinationPath = [IO.Path]::GetFullPath($Destination)
if ($destinationPath -ne (Join-Path $repo 'engine')) { throw 'Engine destination must be the repository engine directory.' }
$downloads = Join-Path $repo 'build/downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
$tag = 'b12121'
$assets = @(
    @{ Name="llama-$tag-bin-win-vulkan-x64.zip"; Hash='73111d348dcbfa86050611c28b8bef523c849d80a6e5529e25b67f1b2055e918'; Folder='vulkan' },
    @{ Name="llama-$tag-bin-win-cuda-13.4-x64.zip"; Hash='7b5676ad9fa4ecd3de2605e3f48edbdcd44dad6c45c8e2656eeebe8725007479'; Folder='cuda' },
    @{ Name='cudart-llama-bin-win-cuda-13.4-x64.zip'; Hash='738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668'; Folder='cuda' }
)
# Download and verify everything before replacing a previously working engine.
foreach ($asset in $assets) {
    $archive = Join-Path $downloads $asset.Name
    if (!(Test-Path -LiteralPath $archive)) {
        $partial = $archive + '.part'
        curl.exe -fLsS --retry 3 -o $partial "https://github.com/ggml-org/llama.cpp/releases/download/$tag/$($asset.Name)"
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $($asset.Name)" }
        Move-Item -LiteralPath $partial -Destination $archive -Force
    }
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $asset.Hash) { throw "Unexpected engine archive hash: $($asset.Name)" }
}
$staging = Join-Path $repo 'build/engine-staging'
if (![IO.Path]::GetFullPath($staging).StartsWith((Join-Path $repo 'build') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid engine staging path' }
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
foreach ($asset in $assets) {
    $extract = Join-Path $staging $asset.Name
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    tar -xf (Join-Path $downloads $asset.Name) -C $extract
    if ($LASTEXITCODE -ne 0) { throw "Extraction failed: $($asset.Name)" }
    $runtime = Join-Path $staging $asset.Folder
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    Get-ChildItem -LiteralPath $extract -Recurse -File | Where-Object {
        $_.Name -match '^(ggml(?:-base|-cpu-[^.]+|-cuda|-vulkan)?|libomp|llama|llama-common|llama-server-impl|mtmd|cudart64_[^.]+|cublas64_[^.]+|cublasLt64_[^.]+)\.dll$|^llama-server\.exe$|^LICENSE|^EULA'
    } | Copy-Item -Destination $runtime -Force
}
$cpu = Join-Path $staging 'cpu'
New-Item -ItemType Directory -Force -Path $cpu | Out-Null
Get-ChildItem -LiteralPath (Join-Path $staging 'vulkan') -File | Where-Object { $_.Name -ne 'ggml-vulkan.dll' } | Copy-Item -Destination $cpu
foreach ($backend in 'cpu','cuda','vulkan') {
    if (!(Test-Path -LiteralPath (Join-Path $staging "$backend/llama-server.exe"))) { throw "Missing $backend server" }
}
$license = Join-Path $staging 'LICENSE-llama.cpp'
curl.exe -fLsS --retry 3 -o $license "https://raw.githubusercontent.com/ggml-org/llama.cpp/$tag/LICENSE"
if ($LASTEXITCODE -ne 0) { throw 'Could not download llama.cpp license' }
foreach ($backend in 'cpu','cuda','vulkan') { Copy-Item -LiteralPath $license -Destination (Join-Path $staging "$backend/LICENSE-llama.cpp") }
$cudaLicense = Join-Path $staging 'cuda/LICENSE-NVIDIA-CUDA.html'
curl.exe -fLsS --retry 3 -o $cudaLicense 'https://docs.nvidia.com/cuda/eula/index.html'
if ($LASTEXITCODE -ne 0) { throw 'Could not download CUDA license' }
# Only the verified, fixed repository engine path may be replaced.
if (Test-Path -LiteralPath $destinationPath) { Remove-Item -LiteralPath $destinationPath -Recurse -Force }
New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
foreach ($backend in 'cpu','cuda','vulkan') { Copy-Item -LiteralPath (Join-Path $staging $backend) -Destination $destinationPath -Recurse }
Write-Output "Prepared CPU / CUDA 13.4 / Vulkan engines ($tag)."
