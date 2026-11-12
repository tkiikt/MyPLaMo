#requires -Version 7.0
param([Parameter(Mandatory)][int]$AppPid,[Parameter(Mandatory)][string]$ProfileDirectory,[Parameter(Mandatory)][string]$ArtifactDirectory)
$ErrorActionPreference='Stop'
$previousWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID='myplamo-features-'+[guid]::NewGuid().ToString('N')
$results=[Collections.Generic.List[object]]::new()
$screenshots=[Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
function Ui([string[]]$Arguments,[string]$Window) {
    $selector=if($Window){@('-w',$Window)}else{@('-a',"$AppPid")}
    $output=@(& winapp ui @Arguments @selector)
    if($LASTEXITCODE -ne 0){throw "winapp ui $($Arguments -join ' '): $($output -join ' ')"}
    $output
}
function Test-Feature([string]$Name,[scriptblock]$Run) {
    try { & $Run | Out-Null; $results.Add(@{name=$Name;status='PASS'}) }
    catch { $results.Add(@{name=$Name;status='FAIL';detail="$_"}); throw }
}
function Shot([string]$Name) {
    $file=Join-Path $ArtifactDirectory $Name
    Ui @('screenshot','-o',$file,'--json') -Window $script:MainWindow | Out-Null
    if(!(Test-Path -LiteralPath $file)){throw "Missing screenshot $file"}
    $screenshots.Add($file)
}
function Select-Item([string]$Combo,[string]$Name) {
    Ui @('invoke',$Combo,'--action','expand') | Out-Null
    $windows=@(Ui @('list-windows','--json') | ConvertFrom-Json)
    $popup=$windows | Where-Object {$_.label -eq 'popup' -and $_.ownerHwnd -eq [long]$script:MainWindow} | Select-Object -First 1
    if(!$popup){throw "No popup for $Combo"}
    Ui @('invoke',$Name,'--type','ListItem','--action','select') -Window "$($popup.hwnd)" | Out-Null
    Ui @('invoke',$Combo,'--action','collapse') | Out-Null
}
function Wait-Text([string]$Id,[string]$Value) { Ui @('wait-for',$Id,'--value',$Value,'--contains','-t','20000') | Out-Null }
function Book-Data { @(Get-Content -LiteralPath (Join-Path $ProfileDirectory 'glossaries.json') -Raw | ConvertFrom-Json) }
try {
    if($ProfileDirectory -notmatch '\\build\\' -or !(Test-Path (Join-Path $ProfileDirectory 'settings.json'))){throw 'Use an isolated build test profile.'}
    $windows=@(Ui @('list-windows','--json') | ConvertFrom-Json)
    $script:MainWindow="$((($windows | Where-Object {$_.className -eq 'WinUIDesktopWin32WindowClass'}) | Select-Object -First 1).hwnd)"
    if(!$script:MainWindow){throw 'No real WinUI window'}
    Test-Feature 'New controls visible and saved glossary retained on launch' {
        foreach($id in 'GlossariesButton','GlossaryChoice','DocumentFormat','UseContext','ContextButton'){Ui @('wait-for',$id,'-p','IsOffscreen','--value','False','-t','3000')}
        $saved=Get-Content (Join-Path $ProfileDirectory 'settings.json') -Raw | ConvertFrom-Json
        if(!$saved.GlossaryId){throw 'Expected selected glossary from previous run'}
        Shot 'feature-main-final.png'
    }
    Test-Feature 'Glossary dialog fits and validation preserves saved data' {
        Ui @('invoke','GlossariesButton'); Ui @('wait-for','SaveTerm','-p','IsOffscreen','--value','False','-t','3000')
        Ui @('wait-for','DeleteTerm','-p','IsOffscreen','--value','False','-t','3000')
        $before=(Book-Data).Count
        Ui @('invoke','SaveTerm'); Wait-Text 'GlossaryMessage' '用語'
        if((Book-Data).Count -ne $before){throw 'Validation altered saved books'}
        Shot 'feature-glossary-final.png'
    }
    Test-Feature 'Multiple glossaries and term CRUD persist' {
        Ui @('invoke','NewGlossary'); Ui @('set-value','GlossaryName','検証用語集'); Ui @('invoke','SaveGlossaryName')
        Ui @('set-value','TermSource','dog'); Ui @('set-value','TermTarget','イヌ'); Ui @('invoke','SaveTerm'); Wait-Text 'GlossaryMessage' '保存しました'
        $books=Book-Data
        if($books.Count -lt 2 -or ($books | Where-Object Name -eq '検証用語集').Terms[0].Target -ne 'イヌ'){throw 'Multiple books or term not saved'}
        Ui @('invoke','英語 → 日本語　 dog → イヌ','--type','ListItem','--action','select'); Ui @('set-value','TermTarget','ワンちゃん'); Ui @('invoke','SaveTerm')
        if(($books=Book-Data | Where-Object Name -eq '検証用語集').Terms.Count -ne 1 -or $books.Terms[0].Target -ne 'ワンちゃん'){throw 'Term edit did not replace original'}
        Ui @('set-value','GlossarySearch','missing'); Ui @('set-value','GlossarySearch','dog')
        Ui @('wait-for','英語 → 日本語　 dog → ワンちゃん','--type','ListItem','-t','3000')
        Ui @('invoke','CloseButton')
    }
    Test-Feature 'Opening manager retains selected translation glossary' {
        $saved=Get-Content (Join-Path $ProfileDirectory 'settings.json') -Raw | ConvertFrom-Json
        $selected=(Book-Data | Where-Object Id -eq $saved.GlossaryId)
        if($selected.Name -ne '動物の用語集'){throw 'Manager reset selected glossary'}
    }
    Test-Feature 'Actual desktop JSON translation preserves keys and uses selected glossary' {
        Select-Item 'DocumentFormat' 'JSON'
        Ui @('set-value','InputText','{"message":"The cat is sleeping.","count":3}')
        Ui @('invoke','TranslateButton'); Wait-Text 'StatusText' '翻訳完了'
        $output=(Ui @('get-value','OutputText','--json') | ConvertFrom-Json).text | ConvertFrom-Json
        if($output.count -ne 3 -or $output.message -notmatch 'ネコちゃん'){throw 'JSON or glossary output wrong'}
        Shot 'feature-json-final.png'
    }
    Test-Feature 'Actual desktop Markdown preserves code and links' {
        Select-Item 'DocumentFormat' 'Markdown'
        Ui @('set-value','InputText',"# Animal news`n`nThe cat is sleeping.`n`n``code```n`n[Website](https://example.com)")
        Ui @('invoke','TranslateButton'); Wait-Text 'StatusText' '翻訳完了'
        $output=(Ui @('get-value','OutputText','--json') | ConvertFrom-Json).text
        if($output -notmatch 'ネコちゃん' -or !$output.Contains('https://example.com')){throw 'Markdown output wrong'}
        Shot 'feature-markdown-final.png'
    }
    Test-Feature 'Context control persists and reference field opens' {
        Ui @('invoke','UseContext','--action','toggle-off'); Ui @('invoke','ContextButton'); Ui @('wait-for','ContextText','-t','3000')
        Ui @('set-value','ContextText','ソフトウェアのヘルプページ')
        Ui @('send-keys','esc','--via','send-input')
        $saved=Get-Content (Join-Path $ProfileDirectory 'settings.json') -Raw | ConvertFrom-Json
        if($saved.UseContext){throw 'Context preference not persisted'}
        Ui @('invoke','UseContext','--action','toggle-on')
    }
    Test-Feature 'Dark theme and glossary dialog remain usable' {
        Ui @('invoke','SettingsButton'); Ui @('wait-for','DarkToggle','-t','3000'); Ui @('invoke','DarkToggle'); Ui @('invoke','PrimaryButton')
        Ui @('wait-for','DarkToggle','--gone','-t','3000'); Shot 'feature-main-dark-final.png'
        Ui @('invoke','GlossariesButton'); Ui @('wait-for','SaveTerm','-p','IsOffscreen','--value','False','-t','3000'); Shot 'feature-glossary-dark-final.png'
        Ui @('invoke','CloseButton')
    }
} catch { if(!($results | Where-Object status -eq 'FAIL')){$results.Add(@{name='Setup';status='FAIL';detail="$_"})} }
finally { & winapp ui yield | Out-Null; $env:WINAPP_UI_WORKFLOW_ID=$previousWorkflow }
$failed=@($results | Where-Object status -eq 'FAIL').Count
@{target='local';appPid=$AppPid;passed=$results.Count-$failed;failed=$failed;results=@($results.ToArray());screenshots=@($screenshots.ToArray())} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ArtifactDirectory 'feature-ui-results.json')
Write-Host "Passed: $($results.Count-$failed), Failed: $failed"
if($failed){exit 1}
