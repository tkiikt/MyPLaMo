#requires -Version 7.0
param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$ProfileDirectory,
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot '../build/gpu-ui-results')
)
$ErrorActionPreference = 'Stop'
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = 'myplamo-gpu-' + [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$memory = [ordered]@{}
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
$settingsPath = Join-Path $ProfileDirectory 'settings.json'
function Ui([string[]]$Arguments) {
    $output = @(& winapp ui @Arguments -a $AppPid)
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $($Arguments -join ' ') failed: $($output -join "`n")" }
    $output
}
function Check([string]$Name,[scriptblock]$Test) {
    try { & $Test | Out-Null; $results.Add(@{name=$Name;status='PASS'}); Write-Host "PASS $Name" }
    catch { $results.Add(@{name=$Name;status='FAIL';detail="$_"}); Write-Host "FAIL $Name : $_" }
}
function WaitValue([string]$Id,[string]$Value,[int]$Timeout=5000) { Ui @('wait-for',$Id,'--value',$Value,'--contains','-t',"$Timeout") }
function Shot([string]$Name) {
    $path = Join-Path $ArtifactDirectory $Name
    Ui @('screenshot','-w',"$script:WindowHandle",'-o',$path,'--focus','--json') | Out-Null
    if (!(Test-Path -LiteralPath $path) -or !(Get-Item -LiteralPath $path).Length) { throw "Missing screenshot: $path" }
    $screenshots.Add($path)
}
function OpenSettings {
    Ui @('wait-for','DeviceCombo','--gone','-t','4000')
    Ui @('invoke','SettingsButton')
    Ui @('wait-for','DeviceCombo','-t','5000')
    WaitValue 'DeviceStatus' 'GPU一覧を更新' 25000
}
function Choose([string]$Label) {
    Ui @('invoke','DeviceCombo'); Ui @('invoke',$Label); WaitValue 'DeviceCombo' $Label
}
function SaveSettings { Ui @('invoke','PrimaryButton'); Ui @('wait-for','DeviceCombo','--gone','-t','4000') }
function CancelSettings { Ui @('invoke','CloseButton'); Ui @('wait-for','DeviceCombo','--gone','-t','4000') }
function Translate([string]$Expected) {
    Ui @('set-value','InputText','The sky is blue.'); Ui @('invoke','TranslateButton')
    WaitValue 'OutputText' '青' 60000; WaitValue 'StatusText' $Expected 10000
}
function Servers { @(Get-CimInstance Win32_Process -Filter "Name='llama-server.exe'" | Where-Object {$_.ParentProcessId -eq $AppPid}) }
function AssertNoServers {
    for ($i=0; $i -lt 20; $i++) { if (!(Servers).Count) { return }; Start-Sleep -Milliseconds 100 }
    throw 'An inference/discovery process survived saving the setting.'
}
function NvidiaMemory { [int]((& nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits | Select-Object -First 1).Trim()) }
try {
    Ui @('wait-for','SettingsButton','-t','10000') | Out-Null
    $script:WindowHandle = (Ui @('list-windows','--json') | ConvertFrom-Json | Where-Object {$_.className -eq 'WinUIDesktopWin32WindowClass'} | Select-Object -First 1).hwnd
    if (!$script:WindowHandle) { throw 'Main window is missing.' }
    $memory.beforeTranslationMiB = NvidiaMemory
    Check 'Default automatic selection and complete GPU list' {
        OpenSettings; WaitValue 'DeviceCombo' '自動'
        Ui @('invoke','DeviceCombo')
        foreach ($name in 'CPUのみ','NVIDIA GeForce RTX 5090 / CUDA','NVIDIA GeForce RTX 5090 / Vulkan','AMD Radeon(TM) Graphics〈内蔵〉 / Vulkan') { Ui @('wait-for',$name,'-t','3000') }
        # Capture the visible popup in place, not a stitched/composite image.
        $popup = Join-Path $ArtifactDirectory '01-device-list.png'
        Ui @('screenshot','-w',"$script:WindowHandle",'-o',$popup,'--capture-screen','--json') | Out-Null
        if (!(Test-Path -LiteralPath $popup)) { throw 'Dropdown screenshot missing.' }; $screenshots.Add($popup)
        Ui @('invoke','NVIDIA GeForce RTX 5090 / CUDA'); SaveSettings
        $saved=Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($saved.ComputeDevice -notlike 'cuda:NVIDIA*') { throw 'CUDA setting was not persisted.' }
    }
    Check 'Selected CUDA translates and reports the GPU name' {
        Translate 'RTX 5090 / CUDA'; Shot '02-cuda-translation.png'
        $memory.cudaLoadedMiB = NvidiaMemory
    }
    Check 'Saving CPU releases the loaded CUDA process immediately' {
        $old=@(Servers | ForEach-Object {$_.ProcessId})
        if ($old.Count -ne 1) { throw 'Expected one loaded inference process.' }
        OpenSettings; Choose 'CPUのみ'; WaitValue 'DeviceHint' '画像生成'; Shot '03-cpu-setting.png'; SaveSettings
        AssertNoServers
        if (($old | ForEach-Object {Get-Process -Id $_ -ErrorAction SilentlyContinue}).Count) { throw 'Previous GPU process is still alive.' }
        $memory.afterCpuSaveMiB = NvidiaMemory
    }
    Check 'CPU-only translation uses the CPU runtime' {
        Translate 'CPU'; Shot '04-cpu-translation.png'
        $server=@(Servers)
        if ($server.Count -ne 1 -or $server[0].ExecutablePath -notlike '*\engine\cpu\llama-server.exe') { throw 'CPU-only setting used a GPU engine.' }
        $memory.cpuLoadedMiB = NvidiaMemory
    }
    Check 'Integrated GPU selection persists and runs through Vulkan' {
        OpenSettings; Choose 'AMD Radeon(TM) Graphics〈内蔵〉 / Vulkan'; WaitValue 'DeviceHint' '共有メモリ'; SaveSettings
        Translate 'Graphics〈内蔵〉 / Vulkan'; Shot '05-integrated-translation.png'
        OpenSettings; WaitValue 'DeviceCombo' 'Graphics〈内蔵〉 / Vulkan'
        Ui @('invoke','RefreshDevicesButton'); WaitValue 'DeviceStatus' 'GPU一覧を更新' 25000
        WaitValue 'DeviceCombo' 'Graphics〈内蔵〉 / Vulkan'; CancelSettings
    }
    Check 'Cancel leaves the saved device and loaded model unchanged' {
        $old=@(Servers | ForEach-Object {$_.ProcessId})
        OpenSettings; Choose 'CPUのみ'; CancelSettings
        $saved=Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($saved.ComputeDevice -notlike 'vulkan:AMD*') { throw 'Cancel changed the persisted device.' }
        if (@(Servers | Where-Object {$_.ProcessId -in $old}).Count -ne 1) { throw 'Cancel unloaded the selected model.' }
    }
    Check 'Manual GeForce Vulkan override runs on the selected adapter' {
        OpenSettings; Choose 'NVIDIA GeForce RTX 5090 / Vulkan'; SaveSettings
        Translate 'RTX 5090 / Vulkan'; Shot '06-vulkan-translation.png'
    }
    Check 'Dark settings remain readable and keep the selected device' {
        OpenSettings; Ui @('scroll-into-view','DarkToggle'); Ui @('invoke','DarkToggle'); SaveSettings
        OpenSettings; WaitValue 'DeviceCombo' 'RTX 5090 / Vulkan'; Shot '07-settings-dark.png'; CancelSettings
    }
    Check 'Automatic setting is restored and selects CUDA' {
        OpenSettings; Choose '自動（GPU優先）'; SaveSettings; Translate 'RTX 5090 / CUDA'; Shot '08-automatic-translation.png'
        # End without retaining model VRAM after the UI test.
        OpenSettings; Choose 'CPUのみ'; SaveSettings; AssertNoServers
    }
    Check 'No unhandled application exceptions' {
        $log=Join-Path $ProfileDirectory 'error.log'
        if ((Test-Path -LiteralPath $log) -and (Get-Item -LiteralPath $log).Length) { throw 'Application error.log is not empty.' }
    }
} catch { $results.Add(@{name='Test setup/execution';status='FAIL';detail="$_"}) }
finally { & winapp ui yield | Out-Null; $env:WINAPP_UI_WORKFLOW_ID=$previousWorkflow }
$failed=@($results | Where-Object {$_.status -eq 'FAIL'}).Count
$report=[ordered]@{target='local';appPid=$AppPid;passed=$results.Count-$failed;failed=$failed;results=$results.ToArray();nvidiaMemory=$memory;screenshots=$screenshots.ToArray()}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
$report | ConvertTo-Json -Depth 6
if ($failed) { exit 1 }
