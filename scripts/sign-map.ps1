#requires -Version 7
<#
.SYNOPSIS
    The single source of truth for which PE files Snaply signs, and their flat
    staging names. Dot-sourced by sign-stage.ps1 and sign-collect.ps1.

.DESCRIPTION
    Every one of Snaply's own PE files is Authenticode-signed — the three
    apphost/launcher exes AND the first-party managed assemblies that carry the
    actual logic (Core / Application / Platform / the app itself). The bundled
    .NET / Windows App SDK / ASP.NET Core runtime DLLs are already
    Microsoft-signed and are deliberately NOT re-signed (that would waste the
    signing quota and claim authorship we don't have).

    The map is bundle-relative-path -> unique flat stage name. Authenticode
    lives inside the PE, so copying under a different name and back is safe. Most
    basenames are already distinct; the explicit map keeps staging robust and
    handles the one collision below.
#>

# Ordered so the layout reads root-first, then app/.
# Note: the root launcher (Snaply.exe) and the CLI apphost (app/snaply.exe) differ only by
# case, which would collide as flat stage names on a case-insensitive filesystem — so the CLI
# stages under a distinct name (Snaply.Cli.exe). The first-party managed DLLs all have distinct
# basenames, so they stage under their own names.
$script:SnaplyFirstPartyPes = [ordered]@{
    'Snaply.exe'                 = 'Snaply.exe'
    'app/Snaply.App.exe'         = 'Snaply.App.exe'
    'app/snaply.exe'             = 'Snaply.Cli.exe'
    'app/Snaply.App.dll'         = 'Snaply.App.dll'
    'app/Snaply.Core.dll'        = 'Snaply.Core.dll'
    'app/Snaply.Application.dll' = 'Snaply.Application.dll'
    'app/Snaply.Platform.dll'    = 'Snaply.Platform.dll'
}
