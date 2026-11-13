#requires -Version 7.0
param([Parameter(Mandatory)][int]$AppPid,[Parameter(Mandatory)][string]$ProfileDirectory,[Parameter(Mandatory)][string]$ArtifactDirectory)
$ErrorActionPreference='Stop'
$previousWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID='myplamo-backup-'+[guid]::NewGuid().ToString('N')
$results=[Collections.Generic.List[object]]::new()
$screenshots=[Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
function Checked([string[]]$Arguments) {
    $output=@(& winapp @Arguments)
    if($LASTEXITCODE -ne 0){throw "winapp $($Arguments -join ' '): $($output -join ' ')"}
    $output
}
function Ui([string[]]$Arguments,[string]$Window=$script:MainWindow) { Checked (@('ui')+$Arguments+@('-w',$Window)) }
function Test-Backup([string]$Name,[scriptblock]$Run) {
    try { & $Run | Out-Null; $results.Add(@{name=$Name;status='PASS'}) }
    catch { $results.Add(@{name=$Name;status='FAIL';detail="$_"}); throw }
}
function Wait-Text([string]$Id,[string]$Text) { Ui @('wait-for',$Id,'--value',$Text,'--contains','-t','5000') }
function Books { @(Get-Content -LiteralPath (Join-Path $ProfileDirectory 'glossaries.json') -Raw | ConvertFrom-Json) }
function Canonical($Books) {
    @($Books | ForEach-Object { [ordered]@{Id=$_.Id;Name=$_.Name;Terms=@($_.Terms | Select-Object Source,Target,From,To)} }) | ConvertTo-Json -Depth 8 -Compress
}
function Shot([string]$Name) {
    $path=Join-Path $ArtifactDirectory $Name
    Ui @('screenshot','-o',$path,'--json') | Out-Null
    if(!(Test-Path -LiteralPath $path) -or !(Get-Item -LiteralPath $path).Length){throw 'Screenshot missing'}
    $screenshots.Add($path)
}
function Picker([string]$Action,[string]$Path,[switch]$Cancel) {
    Ui @('invoke',$(if($Action -eq 'バックアップ'){'BackupGlossaries'}else{'RestoreGlossaries'})) | Out-Null
    $picker=$null
    for($i=0;$i -lt 20;$i++) {
        $picker=@(Checked @('ui','list-windows','--json') | ConvertFrom-Json) |
            Where-Object {$_.ownerHwnd -eq [long]$script:MainWindow -and $_.className -eq '#32770'} | Select-Object -First 1
        if($picker){break}; Start-Sleep -Milliseconds 100
    }
    if(!$picker){throw 'File picker did not open'}
    if($Cancel){Ui @('invoke','キャンセル','--type','Button') -Window "$($picker.hwnd)" | Out-Null}
    else {
        if($Action -eq 'バックアップ') { Ui @('set-value','FileNameControlHost',$Path) -Window "$($picker.hwnd)" | Out-Null }
        else { Ui @('set-value','1148',$Path,'--type','Edit') -Window "$($picker.hwnd)" | Out-Null }
        Ui @('invoke',$Action,'--type','Button') -Window "$($picker.hwnd)" | Out-Null
    }
}
try {
    if($ProfileDirectory -notmatch '\\build\\' -or !(Test-Path (Join-Path $ProfileDirectory 'settings.json'))){throw 'Use an isolated build test profile'}
    $windows=@(Checked @('ui','list-windows','-a',"$AppPid",'--json') | ConvertFrom-Json)
    $script:MainWindow="$((($windows | Where-Object className -eq 'WinUIDesktopWin32WindowClass') | Select-Object -First 1).hwnd)"
    if(!$script:MainWindow){throw 'No local app window'}
    if((Books).Count -ne 2){throw 'Expected isolated two-book fixture'}
    $backup=Join-Path $ArtifactDirectory 'all-glossaries.myplamo-glossary'
    $before=Get-Content (Join-Path $ProfileDirectory 'glossaries.json') -Raw
    Test-Backup 'Backup and restore controls are visible; saved editor stays usable' {
        foreach($id in 'BackupGlossaries','RestoreGlossaries','SaveTerm','DeleteTerm'){
            Ui @('wait-for',$id,'-p','IsOffscreen','--value','False','-t','3000')
        }
        Shot '01-glossary-light.png'
    }
    Test-Backup 'Cancel both file pickers without changing data' {
        Picker 'バックアップ' '' -Cancel
        Ui @('wait-for','BackupGlossaries','-p','IsEnabled','--value','True','-t','3000')
        Picker '選択' '' -Cancel
        Ui @('wait-for','BackupGlossaries','-p','IsEnabled','--value','True','-t','3000')
        if((Get-Content (Join-Path $ProfileDirectory 'glossaries.json') -Raw) -cne $before){throw 'Cancel changed data'}
    }
    Test-Backup 'Save all books using native file picker' {
        Picker 'バックアップ' $backup
        Wait-Text 'GlossaryMessage' '2件の用語集をバックアップしました'
        if(!(Test-Path -LiteralPath $backup) -or (Get-Item $backup).Length -lt 48){throw 'No backup file'}
        if((Get-Content (Join-Path $ProfileDirectory 'glossaries.json') -Raw) -cne $before){throw 'Backup changed data'}
    }
    Test-Backup 'Empty store can restore; confirmation cancel leaves it empty' {
        for($i=0;$i -lt 2;$i++) {Ui @('invoke','DeleteGlossary'); Ui @('invoke','削除する','--type','Button')}
        if((Books).Count -ne 0){throw 'Fixture books not deleted'}
        Ui @('wait-for','BackupGlossaries','-p','IsEnabled','--value','False','-t','3000')
        Picker '選択' $backup; Wait-Text 'RestoreSummary' '追加する用語集：2件（2語）'
        Shot '02-restore-preview.png'
        Ui @('invoke','CancelRestoreGlossaries')
        if((Books).Count -ne 0){throw 'Canceled confirmation changed books'}
    }
    Test-Backup 'Restore recovers exact saved books and directions' {
        Picker '選択' $backup; Wait-Text 'RestoreSummary' '追加する用語集：2件'
        Ui @('invoke','ConfirmRestoreGlossaries'); Wait-Text 'GlossaryMessage' '2件の用語集を復元しました'
        if((Canonical (Books)) -cne (Canonical ($before | ConvertFrom-Json))){throw 'Restored content differs'}
        Shot '03-restored.png'
    }
    Test-Backup 'Repeated restore skips identical books and disables confirm' {
        Picker '選択' $backup; Wait-Text 'RestoreSummary' 'スキップ：2件'
        Ui @('wait-for','ConfirmRestoreGlossaries','-p','IsEnabled','--value','False','-t','3000')
        Shot '04-identical-skipped.png'; Ui @('invoke','CancelRestoreGlossaries')
        if((Books).Count -ne 2){throw 'Duplicate books were added'}
    }
    Test-Backup 'ID collision preserves edited book and restores another copy' {
        Ui @('set-value','GlossaryName','変更した用語集'); Ui @('invoke','SaveGlossaryName')
        Picker '選択' $backup; Wait-Text 'RestoreSummary' '追加する用語集：1件'
        Ui @('invoke','ConfirmRestoreGlossaries'); Wait-Text 'GlossaryMessage' '1件の用語集を復元しました'
        $books=Books
        if($books.Count -ne 3 -or ($books | Where-Object Id -eq 'animals').Name -ne '変更した用語集' -or !($books | Where-Object {$_.Name -eq '動物の用語集' -and $_.Id -ne 'animals'})){throw 'Collision replaced original'}
    }
    Test-Backup 'Corrupt backup displays error and preserves books' {
        $bad=Join-Path $ArtifactDirectory 'broken.myplamo-glossary'; [IO.File]::WriteAllText($bad,'broken')
        $snapshot=Get-Content (Join-Path $ProfileDirectory 'glossaries.json') -Raw
        Picker '選択' $bad; Wait-Text 'GlossaryMessage' '復元できませんでした'
        if((Get-Content (Join-Path $ProfileDirectory 'glossaries.json') -Raw) -cne $snapshot){throw 'Bad file changed store'}
        Shot '05-corrupt-error.png'
    }
    Test-Backup 'Dark theme keeps backup and restore controls readable' {
        Ui @('invoke','CloseButton'); Ui @('wait-for','GlossaryName','--gone','-t','3000')
        Ui @('invoke','SettingsButton'); Ui @('wait-for','DarkToggle','-t','3000')
        Ui @('invoke','DarkToggle'); Ui @('invoke','PrimaryButton'); Ui @('wait-for','DarkToggle','--gone','-t','3000')
        Ui @('invoke','GlossariesButton'); Ui @('wait-for','BackupGlossaries','-t','3000'); Shot '06-glossary-dark.png'
        Picker '選択' $backup; Wait-Text 'RestoreSummary' '追加する用語集：0件'
        Shot '07-restore-dark.png'; Ui @('invoke','CancelRestoreGlossaries'); Ui @('invoke','CloseButton')
        $settings=Get-Content (Join-Path $ProfileDirectory 'settings.json') -Raw | ConvertFrom-Json
        if($settings.GlossaryId -ne 'animals'){throw 'Existing translation selection changed'}
    }
} catch { if(!($results | Where-Object status -eq 'FAIL')){$results.Add(@{name='Setup';status='FAIL';detail="$_"})} }
finally { & winapp ui yield | Out-Null; $env:WINAPP_UI_WORKFLOW_ID=$previousWorkflow }
$failed=@($results | Where-Object status -eq 'FAIL').Count
@{target='local';appPid=$AppPid;passed=$results.Count-$failed;failed=$failed;results=@($results.ToArray());screenshots=@($screenshots.ToArray())} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ArtifactDirectory 'results.json')
Write-Host "Passed: $($results.Count-$failed), Failed: $failed"
if($failed){exit 1}
