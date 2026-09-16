@{
    ExcludeRules = @(
        # Write-Host is intentional in this repo's scripts. capture-state.ps1,
        # diagnose-driver.ps1, mm-bt-stack-snapshot.ps1 and verify-release.ps1 are
        # interactive diagnostic/installer tools whose coloured console output IS the
        # product; they are never piped, so Write-Output would break the UX.
        'PSAvoidUsingWriteHost',

        # The state-changing helpers in these scripts (Stop-/Remove-/Set- style
        # operations) are script-internal, not exported cmdlets in a module, so
        # -WhatIf/-Confirm ShouldProcess plumbing would add ceremony with no caller
        # able to use it.
        'PSUseShouldProcessForStateChangingFunctions'
    )

    Rules = @{
        # Pin the "built-in cmdlet" baseline to Windows PowerShell 5.1. The shipped
        # scripts run there (package-release/sign-app/verify-release declare
        # `#Requires -Version 5`; diagnose-driver and kbd-patch-cachedservices run
        # elevated via powershell.exe); the CI-only helpers declare `#Requires
        # -Version 7`, and the 5.1 command set is the safer baseline for them too,
        # since it is a superset of the PS7 name space for collision purposes.
        # The default baseline is PowerShell Core 6.1.0, whose profile lists
        # PSDesiredStateConfiguration's *internal* helper function `Write-Log` as a
        # shipped command -- it is not a cmdlet, it does not exist in PowerShell 5.1
        # or 7, and `Get-Command Write-Log` finds nothing. Pinning the baseline keeps
        # this rule enforcing real collisions instead of excluding it outright.
        PSAvoidOverwritingBuiltInCmdlets = @{
            PowerShellVersion = @('desktop-5.1.14393.206-windows')
        }
    }
}
