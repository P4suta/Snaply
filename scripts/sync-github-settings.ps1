#requires -Version 7.0
param(
    [Parameter(ParameterSetName = 'Check', Mandatory)]
    [switch]$Check,

    [Parameter(ParameterSetName = 'Apply', Mandatory)]
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rulesetRoot = Join-Path $root '.github\rulesets'
$allowedActionPatterns = @(
    'SSLcom/esigner-codesign@*',
    'crate-ci/typos@*',
    'fsfe/reuse-action@*',
    'googleapis/release-please-action@*',
    'microsoft/setup-WinAppCli@*',
    'ossf/scorecard-action@*',
    'raven-actions/actionlint@*',
    'softprops/action-gh-release@*',
    'taiki-e/install-action@*'
)

function Invoke-Gh {
    param([string[]]$Arguments)

    $output = gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh failed: $($Arguments -join ' ')"
    }

    return $output
}

function ConvertTo-NormalForm {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [string] -or $Value.GetType().IsValueType) {
        return $Value
    }

    if ($Value -is [System.Collections.IEnumerable] -and
        $Value -isnot [System.Management.Automation.PSCustomObject]) {
        return @($Value | ForEach-Object { ConvertTo-NormalForm $_ })
    }

    $normal = [ordered]@{}
    foreach ($property in $Value.PSObject.Properties | Sort-Object Name) {
        $normal[$property.Name] = ConvertTo-NormalForm $property.Value
    }

    return [pscustomobject]$normal
}

function Get-RulesetPayload {
    param([object]$Ruleset)

    return [pscustomobject][ordered]@{
        name = $Ruleset.name
        target = $Ruleset.target
        enforcement = $Ruleset.enforcement
        bypass_actors = @($Ruleset.bypass_actors)
        conditions = $Ruleset.conditions
        rules = @($Ruleset.rules)
    }
}

$repository = (Invoke-Gh @(
        'repo', 'view',
        '--json', 'nameWithOwner',
        '--jq', '.nameWithOwner')).Trim()
$liveRulesets = Invoke-Gh @(
    'api', "repos/$repository/rulesets",
    '--paginate') | ConvertFrom-Json

if ($Apply) {
    foreach ($path in Get-ChildItem -LiteralPath $rulesetRoot -Filter '*.json' -File) {
        $canonical = Get-Content $path.FullName -Raw | ConvertFrom-Json
        $live = @($liveRulesets | Where-Object name -eq $canonical.name)
        if ($live.Count -gt 1) {
            throw "Multiple live rulesets are named '$($canonical.name)'."
        }

        if ($live.Count -eq 1) {
            Invoke-Gh @(
                'api',
                '--method', 'PUT',
                "repos/$repository/rulesets/$($live[0].id)",
                '--input', $path.FullName) | Out-Null
        }
        else {
            Invoke-Gh @(
                'api',
                '--method', 'POST',
                "repos/$repository/rulesets",
                '--input', $path.FullName) | Out-Null
        }
    }

    $liveRulesets = Invoke-Gh @(
        'api', "repos/$repository/rulesets",
        '--paginate') | ConvertFrom-Json

    Invoke-Gh @(
        'api',
        '--method', 'PUT',
        "repos/$repository/actions/permissions",
        '-F', 'enabled=true',
        '-f', 'allowed_actions=selected',
        '-F', 'sha_pinning_required=true') | Out-Null
    $selectedActionArguments = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @(
            'api',
            '--method', 'PUT',
            "repos/$repository/actions/permissions/selected-actions",
            '-F', 'github_owned_allowed=true',
            '-F', 'verified_allowed=false')) {
        $selectedActionArguments.Add($argument)
    }
    foreach ($pattern in $allowedActionPatterns) {
        $selectedActionArguments.Add('-f')
        $selectedActionArguments.Add("patterns_allowed[]=$pattern")
    }

    Invoke-Gh $selectedActionArguments.ToArray() | Out-Null
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($path in Get-ChildItem -LiteralPath $rulesetRoot -Filter '*.json' -File) {
    $canonical = Get-Content $path.FullName -Raw | ConvertFrom-Json
    $summary = @($liveRulesets | Where-Object name -eq $canonical.name)
    if ($summary.Count -ne 1) {
        $failures.Add("Ruleset '$($canonical.name)' is missing or duplicated.")
        continue
    }

    $live = Invoke-Gh @(
        'api',
        "repos/$repository/rulesets/$($summary[0].id)") | ConvertFrom-Json
    $expectedJson = ConvertTo-NormalForm (Get-RulesetPayload $canonical) |
        ConvertTo-Json -Depth 100 -Compress
    $actualJson = ConvertTo-NormalForm (Get-RulesetPayload $live) |
        ConvertTo-Json -Depth 100 -Compress
    if ($expectedJson -ne $actualJson) {
        $failures.Add("Ruleset '$($canonical.name)' differs from its canonical JSON.")
    }
}

$variables = @(Invoke-Gh @(
        'variable', 'list',
        '--repo', $repository,
        '--json', 'name') | ConvertFrom-Json)
if ('MSIX_PUBLISHER' -notin @($variables | ForEach-Object name)) {
    $failures.Add('Repository variable MSIX_PUBLISHER is missing.')
}

$repositoryState = Invoke-Gh @(
    'api', "repos/$repository") | ConvertFrom-Json
foreach ($feature in @('secret_scanning', 'secret_scanning_push_protection')) {
    if ($repositoryState.security_and_analysis.$feature.status -ne 'enabled') {
        $failures.Add("$feature is not enabled.")
    }
}

$actionsPermissions = Invoke-Gh @(
    'api', "repos/$repository/actions/permissions") | ConvertFrom-Json
if ($actionsPermissions.allowed_actions -ne 'selected') {
    $failures.Add('Actions are not restricted to the selected allowlist.')
}
else {
    $actionPolicy = Invoke-Gh @(
        'api', "repos/$repository/actions/permissions/selected-actions") |
        ConvertFrom-Json
    if (-not $actionPolicy.github_owned_allowed) {
        $failures.Add('GitHub-owned Actions are not allowed.')
    }

    if ($actionPolicy.verified_allowed) {
        $failures.Add('All verified Actions are allowed instead of the explicit allowlist.')
    }

    if (-not $actionsPermissions.sha_pinning_required) {
        $failures.Add('Actions SHA pinning is not required.')
    }

    $expectedPatterns = @($allowedActionPatterns | Sort-Object)
    $actualPatterns = @($actionPolicy.patterns_allowed | Sort-Object)
    if (Compare-Object $expectedPatterns $actualPatterns) {
        $failures.Add('The selected Actions allowlist differs from the canonical list.')
    }
}

if ($failures.Count -ne 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    throw "GitHub settings check failed with $($failures.Count) issue(s)."
}

Write-Host "GitHub settings match the canonical release policy for $repository."
