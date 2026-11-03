# Builds the sample and runs it in one Windows Terminal tab, a row per site:
#
#   Router  | Website  | Sales             sales
#           | Billing  | PaymentGateway    billing
#   CLI     | Shipping | ParcelDelivery    shipping
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'

if (-not $NoBuild) {
    dotnet build "$PSScriptRoot\Topology2.sln" -nologo -v q
    if ($LASTEXITCODE) { throw 'The build failed. If files are locked, stop the running sample first.' }
}

function Pane([string]$Path, [string]$Title = (Split-Path $Path -Leaf), [string]$Command = 'dotnet run --no-build') {
    '-d', (Join-Path $PSScriptRoot $Path), '--title', $Title, '--suppressApplicationTitle', 'pwsh', '-NoExit', '-Command', $Command
}

wt -w 0 @(
    'new-tab'; Pane Router
    ';'; 'split-pane', '-V', '--size', '0.7'; Pane Website
    ';'; 'split-pane', '-H', '--size', '0.67'; Pane Billing
    ';'; 'split-pane', '-H', '--size', '0.5'; Pane Shipping
    ';'; 'split-pane', '-V'; Pane ParcelDelivery
    ';'; 'move-focus', 'up'
    ';'; 'split-pane', '-V'; Pane PaymentGateway
    ';'; 'move-focus', 'up'
    ';'; 'split-pane', '-V'; Pane Sales
    ';'; 'move-focus', 'first'
    ';'; 'split-pane', '-H', '--size', '0.7'; Pane . CLI ". '$PSScriptRoot\routing.ps1'"
)
