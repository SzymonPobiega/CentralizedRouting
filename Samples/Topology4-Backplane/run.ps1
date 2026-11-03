# Builds the sample and runs it in one Windows Terminal tab, a column per part:
#
#   SalesRouter | BillingRouter  | ShippingRouter
#   Website     | Billing        | Shipping
#   Sales       | PaymentGateway | ParcelDelivery
#   CLI
#
# -Backplane picks the routers' launch profile: Duplicating sends about half the messages over the backplane twice,
# NoDeduplication does that and also turns off deduplication.
param([switch]$NoBuild, [ValidateSet('Default', 'Duplicating', 'NoDeduplication')][string]$Backplane = 'Default')
$ErrorActionPreference = 'Stop'

if (-not $NoBuild) {
    dotnet build "$PSScriptRoot\Topology4.sln" -nologo -v q
    if ($LASTEXITCODE) { throw 'The build failed. If files are locked, stop the running sample first.' }
}

function Pane([string]$Path, [string]$Title = (Split-Path $Path -Leaf), [string]$Command = 'dotnet run --no-build') {
    '-d', (Join-Path $PSScriptRoot $Path), '--title', $Title, '--suppressApplicationTitle', 'pwsh', '-NoExit', '-Command', $Command
}

$router = switch ($Backplane) {
    'Default' { 'dotnet run --no-build' }
    'Duplicating' { "dotnet run --no-build --launch-profile 'Duplicating backplane'" }
    'NoDeduplication' { "dotnet run --no-build --launch-profile 'Duplicating backplane, no deduplication'" }
}

wt -w 0 @(
    'new-tab'; Pane SalesRouter -Command $router
    ';'; 'split-pane', '-H', '--size', '0.25'; Pane . CLI ". '$PSScriptRoot\routing.ps1'"
    ';'; 'move-focus', 'up'
    ';'; 'split-pane', '-V', '--size', '0.67'; Pane BillingRouter -Command $router
    ';'; 'split-pane', '-V', '--size', '0.5'; Pane ShippingRouter -Command $router
    ';'; 'split-pane', '-H', '--size', '0.67'; Pane Shipping
    ';'; 'split-pane', '-H', '--size', '0.5'; Pane ParcelDelivery
    ';'; 'move-focus', 'left'
    ';'; 'split-pane', '-H', '--size', '0.67'; Pane Billing
    ';'; 'split-pane', '-H', '--size', '0.5'; Pane PaymentGateway
    ';'; 'move-focus', 'left'
    ';'; 'split-pane', '-H', '--size', '0.67'; Pane Website
    ';'; 'split-pane', '-H', '--size', '0.5'; Pane Sales
    ';'; 'move-focus', 'down'
)
