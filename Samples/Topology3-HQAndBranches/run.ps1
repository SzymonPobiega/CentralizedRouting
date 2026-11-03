# Builds the sample and runs it in two Windows Terminal tabs (Ctrl+Tab switches between them):
#
#   HQ:        Router  | Website | Sales       Branches:  BranchA router | BranchB router
#              --------+---------+---------               Warehouse A    | Warehouse B
#              CLI     | Billing | Shipping              Backorder A    | Backorder B
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'

if (-not $NoBuild) {
    dotnet build "$PSScriptRoot\Topology3.sln" -nologo -v q
    if ($LASTEXITCODE) { throw 'The build failed. If files are locked, stop the running sample first.' }
}

function Pane([string]$Path, [string]$Title = (Split-Path $Path -Leaf), [string]$Command = 'dotnet run --no-build') {
    '-d', (Join-Path $PSScriptRoot $Path), '--title', $Title, '--suppressApplicationTitle', 'pwsh', '-NoExit', '-Command', $Command
}

wt -w 0 @(
    'new-tab'; Pane HQ\Router 'HQ router'
    ';'; 'split-pane', '-V', '--size', '0.7'; Pane HQ\Website
    ';'; 'split-pane', '-H'; Pane HQ\Billing
    ';'; 'split-pane', '-V'; Pane HQ\Shipping
    ';'; 'move-focus', 'up'
    ';'; 'split-pane', '-V'; Pane HQ\Sales
    ';'; 'move-focus', 'first'
    ';'; 'split-pane', '-H', '--size', '0.7'; Pane . CLI ". '$PSScriptRoot\routing.ps1'"

    ';'; 'new-tab'; Pane BranchA\Router 'BranchA router'
    ';'; 'split-pane', '-V'; Pane BranchB\Router 'BranchB router'
    ';'; 'split-pane', '-H', '--size', '0.67'; Pane BranchB\Warehouse 'Warehouse B'
    ';'; 'split-pane', '-H', '--size', '0.5'; Pane BranchB\Backorder 'Backorder B'
    ';'; 'move-focus', 'left'
    ';'; 'split-pane', '-H', '--size', '0.67'; Pane BranchA\Warehouse 'Warehouse A'
    ';'; 'split-pane', '-H', '--size', '0.5'; Pane BranchA\Backorder 'Backorder A'
    ';'; 'focus-tab', '--previous'
)
