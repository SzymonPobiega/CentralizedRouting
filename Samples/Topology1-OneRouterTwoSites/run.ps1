# Builds the sample and runs it in one Windows Terminal tab:
#
#   Router  | Website  | Sales       on-premises (SQL Server)
#   --------+----------+---------
#   CLI     | Billing  | Shipping    azure
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'

if (-not $NoBuild) {
    dotnet build "$PSScriptRoot\Topology1.sln" -nologo -v q
    if ($LASTEXITCODE) { throw 'The build failed. If files are locked, stop the running sample first.' }
}

function Pane([string]$Path, [string]$Title = (Split-Path $Path -Leaf), [string]$Command = 'dotnet run --no-build') {
    '-d', (Join-Path $PSScriptRoot $Path), '--title', $Title, '--suppressApplicationTitle', 'pwsh', '-NoExit', '-Command', $Command
}

wt -w 0 @(
    'new-tab'; Pane Router
    ';'; 'split-pane', '-V', '--size', '0.7'; Pane Website
    ';'; 'split-pane', '-H'; Pane Billing
    ';'; 'split-pane', '-V'; Pane Shipping
    ';'; 'move-focus', 'up'
    ';'; 'split-pane', '-V'; Pane Sales
    ';'; 'move-focus', 'first'
    ';'; 'split-pane', '-H', '--size', '0.7'; Pane . CLI ". '$PSScriptRoot\routing.ps1'"
)
