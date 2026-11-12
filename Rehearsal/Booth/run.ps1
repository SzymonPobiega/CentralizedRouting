# Builds the router image and the endpoints, starts the brokers and routers in Docker and runs the endpoints in one
# Windows Terminal tab:
#
#   Client (PostgreSQL) | Sales
#   --------------------+---------
#   CLI                 | Billing
#                       | Shipping
#
# Needs Docker and the AzureServiceBus_ConnectionString environment variable. Stop with: docker compose down
param([switch]$NoBuild, [switch]$RabbitMQClient)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\..")

if (-not $NoBuild) {
    # Docker without the containerd image store loads only single-platform images
    dotnet publish "$root\NServiceBus.Community.Routing.Router.Host" -c Release -r linux-x64 -t:PublishContainer -p:ContainerRuntimeIdentifiers= -nologo -v q
    if ($LASTEXITCODE) { throw 'Building the router image failed.' }
    dotnet build "$PSScriptRoot\Endpoints" -nologo -v q
    dotnet build "$root\NServiceBus.Community.Routing.Cli" -nologo -v q
    if ($LASTEXITCODE) { throw 'The build failed.' }
}

docker compose -f "$PSScriptRoot\compose.yaml" up -d
if ($LASTEXITCODE) { throw 'Starting the containers failed.' }

$clientTransport = if ($RabbitMQClient) { '' } else { ' --transport postgres' }
function Pane([string]$Title, [string]$Command, [string]$Directory = "$PSScriptRoot\Endpoints") {
    '-d', $Directory, '--title', $Title, '--suppressApplicationTitle', 'pwsh', '-NoExit', '-Command', $Command
}

wt -w 0 @(
    'new-tab'; Pane Client "dotnet run --no-build -- client$clientTransport"
    ';'; 'split-pane', '-V'; Pane Sales 'dotnet run --no-build -- sales'
    ';'; 'split-pane', '-H'; Pane Billing 'dotnet run --no-build -- billing'
    ';'; 'split-pane', '-H'; Pane Shipping 'dotnet run --no-build -- shipping'
    ';'; 'move-focus', 'first'
    ';'; 'split-pane', '-H'; Pane CLI ". .\routing.ps1; Write-Host 'Once all endpoints run, assign the routes: .\setup.ps1'" $PSScriptRoot
)
