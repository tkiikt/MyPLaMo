#requires -Version 5.1
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$msiPath = (Resolve-Path -LiteralPath $Path).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
$summary = $null
try {
    $database = $installer.OpenDatabase($msiPath, 0)
    $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ALLUSERS'")
    try {
        $view.Execute()
        $record = $view.Fetch()
        if ($record -and $record.StringData(1)) { throw 'This fix requires a per-user MSI with ALLUSERS absent or empty.' }
    } finally { $view.Close(); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
    $database = $null

    # WiX 5 Scope=perUser sets the MSI "no elevated privileges required" bit.
    # Retain per-user registration, but let Windows Installer request UAC for its
    # service. This is required for rollback security on custom volumes such as D:.
    # https://learn.microsoft.com/windows/win32/msi/word-count-summary
    $summary = $installer.SummaryInformation($msiPath, 1)
    $wordCount = [int]$summary.Property(15)
    $summary.Property(15) = $wordCount -band (-bnot 8)
    $summary.Persist()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) | Out-Null
    $summary = $installer.SummaryInformation($msiPath, 0)
    if (([int]$summary.Property(15) -band 8) -ne 0) { throw 'The MSI still disables elevation.' }
    Write-Output 'MSI: per-user registration retained; native Windows Installer elevation enabled.'
} finally {
    if ($summary) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) | Out-Null }
    if ($database) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
}
