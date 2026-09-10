param([switch]$Reset, [switch]$BrokenConsole, [switch]$NoOpRules)
$ErrorActionPreference = 'Stop'
$compose = Join-Path $PSScriptRoot 'compose.yaml'
$reports = Join-Path $PSScriptRoot 'reports'
New-Item -ItemType Directory -Force $reports | Out-Null
foreach ($name in @('results.json', 'postgresql-before.json', 'postgresql-after.json', 'postgresql-console.log', 'sqlserver-before.json', 'sqlserver-after.json', 'sqlserver-console.log')) {
    $artifact = Join-Path $reports $name
    if (Test-Path -LiteralPath $artifact) { Remove-Item -LiteralPath $artifact }
}
# Publish a fresh status before any build or dependency can fail.
'<html><meta charset="utf-8"><h1>RuleMaskDb: RUNNING</h1></html>' | Set-Content (Join-Path $reports 'index.html')
function Invoke-Compose {
    & docker compose -f $compose @args
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed ($LASTEXITCODE): $args" }
}
try {
    if ($Reset) { Invoke-Compose down --volumes --remove-orphans }
    Invoke-Compose --profile report up -d report
    Invoke-Compose build runner
    Invoke-Compose up -d --wait --wait-timeout 240 postgres sqlserver
    # Keep Nginx alive when the one-shot runner returns a failure.
    $extra = @()
    if ($BrokenConsole) { $extra = @('-e', 'CLI_PATH=/missing-console.dll') }
    if ($NoOpRules) { $extra += @('-e', 'NO_OP_RULES=1') }
    & docker compose -f $compose run --rm --no-deps @extra runner
    $result = $LASTEXITCODE
    if ($result -ne 0 -and (Get-Content (Join-Path $reports 'index.html') -Raw) -match 'RUNNING') {
        throw "Runner failed before completing its report (exit $result)"
    }
    Write-Host 'Report: http://localhost:8088 (or RULEMASK_REPORT_PORT)'
    exit $result
} catch {
    $message = [System.Net.WebUtility]::HtmlEncode($_.ToString())
    "<html><meta charset='utf-8'><h1>RuleMaskDb: FAIL</h1><pre>$message</pre></html>" | Set-Content (Join-Path $reports 'index.html')
    Write-Error $_ -ErrorAction Continue
    exit 1
}
