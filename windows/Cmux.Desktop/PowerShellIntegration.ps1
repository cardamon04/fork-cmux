# Session-local OSC 7 reporting. No profile, history or execution-policy writes.
if ($ExecutionContext.SessionState.LanguageMode -eq 'FullLanguage' -and
    -not (Microsoft.PowerShell.Utility\Get-Variable -Name __cmux_windows_prompt -Scope Global -ErrorAction Ignore)) {
    $cmuxPrompt = Microsoft.PowerShell.Management\Get-Item Function:\prompt -ErrorAction Ignore
    if ($null -ne $cmuxPrompt -and
        ($cmuxPrompt.Options -band [System.Management.Automation.ScopedItemOptions]::ReadOnly) -eq 0 -and
        ($cmuxPrompt.Options -band [System.Management.Automation.ScopedItemOptions]::Constant) -eq 0) {
        $global:__cmux_windows_prompt = $cmuxPrompt.ScriptBlock
        function global:prompt {
            $cmuxSucceeded = $global:?
            try {
                $cmuxLocation = $ExecutionContext.SessionState.Path.CurrentLocation
                if ($cmuxLocation.Provider.Name -eq 'FileSystem') {
                    $cmuxUri = [System.Uri]::new($cmuxLocation.ProviderPath, [System.UriKind]::Absolute)
                    [Console]::Write(([char]27) + ']7;' + $cmuxUri.AbsoluteUri + ([char]7))
                }
            } catch { }
            # Restore the status observed by the existing prompt without adding
            # an error record or changing LASTEXITCODE. No native command runs here.
            if (-not $cmuxSucceeded) {
                Microsoft.PowerShell.Utility\Write-Error -Message 'cmux command status' -ErrorAction Ignore
            }
            $global:__cmux_windows_prompt.Invoke()
        }
    }
}
