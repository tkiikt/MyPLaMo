#requires -Version 7.0
param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$ProfileDirectory,
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot '../build/winui-ui-results')
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = 'myplamo-winui-' + [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Force $ArtifactDirectory | Out-Null
$ArtifactDirectory = (Resolve-Path $ArtifactDirectory).Path

function Ui([string[]]$Arguments) {
    $output = @(& winapp ui @Arguments -a $AppPid)
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $($Arguments -join ' ') failed: $($output -join "`n")" }
    $output
}
function Check([string]$Name, [scriptblock]$Test) {
    try { & $Test | Out-Null; $results.Add(@{ name=$Name; status='PASS' }) }
    catch { $results.Add(@{ name=$Name; status='FAIL'; detail="$_" }) }
}
function Shot([string]$Name) {
    $file = Join-Path $ArtifactDirectory $Name
    Ui @('screenshot','-w',"$script:WindowHandle",'-o',$file,'--focus','--json') | Out-Null
    if (!(Test-Path -LiteralPath $file) -or !(Get-Item -LiteralPath $file).Length) { throw "Screenshot missing: $file" }
    $screenshots.Add($file)
}
function WaitValue([string]$Id,[string]$Value,[int]$Timeout=5000) { Ui @('wait-for',$Id,'--value',$Value,'--contains','-t',"$Timeout") }
function Property([string]$Id,[string]$Name,[string]$Expected) { Ui @('wait-for',$Id,'-p',$Name,'--value',$Expected,'-t','3000') }
function OpenSettings { Ui @('invoke','SettingsButton'); Ui @('wait-for','InputBox','-t','3000') }
function CloseSettings([string]$Button='PrimaryButton') { Ui @('invoke',$Button); Ui @('wait-for','InputBox','--gone','-t','3000') }

try {
    $settingsFile = Join-Path $ProfileDirectory 'settings.json'
    if (!(Test-Path -LiteralPath $settingsFile)) { throw 'Use an isolated test profile.' }
    Ui @('wait-for','ModelsButton','-t','10000') | Out-Null
    $windows = @(Ui @('list-windows','--json') | ConvertFrom-Json)
    $script:WindowHandle = ($windows | Where-Object {$_.className -eq 'WinUIDesktopWin32WindowClass'} | Select-Object -First 1).hwnd
    if (!$script:WindowHandle) { throw 'No WinUI window found.' }

    Check 'Separate model and settings commands' {
        Ui @('wait-for','ModelsButton','-t','3000'); Ui @('wait-for','SettingsButton','-t','3000')
        Shot '01-main-light.png'
    }
    Check 'Four model cards and visible folder/license' {
        Ui @('invoke','ModelsButton')
        foreach($id in '0.8b','2b','8b','31b') { Ui @('wait-for',"ActivateModel_$id",'-t','3000') }
        Property 'ChooseModelFolderButton' 'IsOffscreen' 'False'; Property 'LicenseConsentCheck' 'IsOffscreen' 'False'
        WaitValue 'ActivateModel_8b' 'ダウンロード'
        Shot '02-models-light.png'
    }
    Check 'License gates model download and selection' {
        Ui @('invoke','LicenseConsentCheck')
        Property 'ActivateModel_0.8b' 'IsEnabled' 'False'; Property 'ActivateModel_8b' 'IsEnabled' 'False'
        Ui @('invoke','LicenseConsentCheck'); Property 'ActivateModel_0.8b' 'IsEnabled' 'True'
    }
    Check 'Switch model after checksum and real test translation' {
        Ui @('invoke','ActivateModel_0.8b')
        WaitValue 'ModelOperationStatus' '完了しました' 60000
        WaitValue 'ActivateModel_0.8b' '使用中'
        $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        if ($settings.ModelId -ne '0.8b') { throw 'Selected model not persisted.' }
        Shot '03-model-selected.png'
        Ui @('invoke','ModelsDoneButton')
    }
    Check 'Published app translates English to Japanese' {
        Ui @('set-value','InputText','The sky is blue.'); Ui @('invoke','TranslateButton')
        WaitValue 'OutputText' '青' 60000; WaitValue 'StatusText' '翻訳完了'
        Shot '04-translation.png'
    }
    Check 'History opens independently' {
        Ui @('invoke','HistoryButton'); Ui @('wait-for','HistoryList','-t','3000')
        Shot '05-history.png'; Ui @('invoke','CloseButton'); Ui @('wait-for','HistoryList','--gone','-t','3000')
    }
    Check 'Settings save independently and apply dark theme immediately' {
        OpenSettings
        Ui @('set-value','InputBox','100'); Ui @('focus','DarkToggle'); Ui @('invoke','DarkToggle')
        CloseSettings
        $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        if ($settings.Limit -ne 100 -or !$settings.Dark -or $settings.ModelId -ne '0.8b') { throw 'Settings were not saved independently.' }
        Shot '06-main-dark.png'
        OpenSettings; Shot '07-settings-dark.png'; CloseSettings 'CloseButton'
    }
    Check 'Input limit rejects oversized text' {
        Ui @('set-value','InputText',('a' * 101)); Ui @('invoke','TranslateButton')
        WaitValue 'StatusText' '100 文字以内'
    }
    Check 'Keyboard accelerator translates' {
        OpenSettings; Ui @('set-value','InputBox','4000'); Ui @('focus','DarkToggle'); CloseSettings
        Ui @('set-value','InputText','The sky is blue.')
        Ui @('send-keys','ctrl+enter','--target','InputText','--via','send-input')
        WaitValue 'OutputText' '青' 60000
    }
    Check 'Escape cancels translation and restores controls' {
        Ui @('set-value','InputText',('The sky is blue. ' * 100)); Ui @('invoke','TranslateButton')
        Ui @('send-keys','esc','--via','send-input')
        WaitValue 'StatusText' 'キャンセルしました' 10000
        Property 'InputText' 'IsReadOnly' 'False'; Property 'ModelsButton' 'IsEnabled' 'True'
    }
    Check 'Dark model dialog retains all operations' {
        Ui @('invoke','ModelsButton'); Ui @('wait-for','MoveModelsButton','-t','3000')
        Shot '08-models-dark.png'; Ui @('invoke','ModelsDoneButton')
    }
    Check 'No unexpected runtime errors' {
        $errorLog = Join-Path $ProfileDirectory 'error.log'
        if ((Test-Path -LiteralPath $errorLog) -and (Get-Item -LiteralPath $errorLog).Length -gt 0) { throw 'Unexpected runtime error log present.' }
    }
} catch { $results.Add(@{name='Test setup/execution';status='FAIL';detail="$_"}) }
finally {
    & winapp ui yield | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
}
$failed = @($results | Where-Object {$_.status -eq 'FAIL'}).Count
$report = [ordered]@{target='local';appPid=$AppPid;passed=$results.Count-$failed;failed=$failed;results=$results.ToArray();screenshots=$screenshots.ToArray()}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
$report | ConvertTo-Json -Depth 6
if ($failed) { exit 1 }
