# Builds the sample and runs it in one Windows Terminal tab, a row per endpoint and a column per site:
#
#   Router  | Website (rabbit)  | Website (azure)
#           | Sales (rabbit)    | Sales (azure)
#   CLI     | Billing (rabbit)  | Billing (azure)
#           | Shipping (rabbit) | Shipping (azure)
#
# The azure instances wait: press Enter in one to move that endpoint.
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'

if (-not $NoBuild) {
    dotnet build "$PSScriptRoot\Topology5.sln" -nologo -v q
    if ($LASTEXITCODE) { throw 'The build failed. If files are locked, stop the running sample first.' }
}

function Pane([string]$Path, [string]$Title = (Split-Path $Path -Leaf), [string]$Command = 'dotnet run --no-build') {
    '-d', (Join-Path $PSScriptRoot $Path), '--title', $Title, '--suppressApplicationTitle', 'pwsh', '-NoExit', '-Command', $Command
}

function Rabbit([string]$Endpoint) { Pane $Endpoint "$Endpoint (rabbit)" 'dotnet run --no-build --launch-profile rabbit' }

function Azure([string]$Endpoint) {
    Pane $Endpoint "$Endpoint (azure)" "Read-Host 'Press Enter to start $Endpoint on Azure Service Bus' | ForEach-Object { dotnet run --no-build --launch-profile azure }"
}

wt -w 0 @(
    'new-tab'; Pane Router
    ';'; 'split-pane', '-V', '--size', '0.75'; Rabbit Website
    ';'; 'split-pane', '-V', '--size', '0.5'; Azure Website
    ';'; 'split-pane', '-H', '--size', '0.75'; Azure Sales
    ';'; 'split-pane', '-H', '--size', '0.67'; Azure Billing
    ';'; 'split-pane', '-H', '--size', '0.5'; Azure Shipping
    ';'; 'move-focus', 'left'
    ';'; 'split-pane', '-H', '--size', '0.75'; Rabbit Sales
    ';'; 'split-pane', '-H', '--size', '0.67'; Rabbit Billing
    ';'; 'split-pane', '-H', '--size', '0.5'; Rabbit Shipping
    ';'; 'move-focus', 'first'
    ';'; 'split-pane', '-H', '--size', '0.7'; Pane . CLI ". '$PSScriptRoot\routing.ps1'"
)
