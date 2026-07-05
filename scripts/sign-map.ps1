#requires -Version 7
<#
.SYNOPSIS
    The single source of truth for which PE files Snaply signs, and their flat
    staging names. Dot-sourced by sign-stage.ps1 and sign-collect.ps1.

.DESCRIPTION
    Only Snaply's own binaries are Authenticode-signed — the bundled .NET /
    Windows App SDK runtime DLLs are already Microsoft-signed, and re-signing
    them would waste the signing quota and claim authorship we don't have.

    The map is bundle-relative-path -> unique flat stage name. Authenticode
    lives inside the PE, so copying under a different name and back is safe. The
    two first-party PEs (root launcher + app apphost) have distinct basenames,
    but the explicit map keeps staging robust if that ever changes.
#>

# Ordered so the layout reads root-first, then app/.
# Note: the root launcher (Snaply.exe) and the CLI apphost (app/snaply.exe) differ only by
# case, which would collide as flat stage names on a case-insensitive filesystem — so the CLI
# stages under a distinct name (Snaply.Cli.exe).
$script:SnaplyFirstPartyPes = [ordered]@{
    'Snaply.exe'         = 'Snaply.exe'
    'app/Snaply.App.exe' = 'Snaply.App.exe'
    'app/snaply.exe'     = 'Snaply.Cli.exe'
}
