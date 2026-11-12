# Defines the routing command line functions for this sample. Dot-source it: . .\routing.ps1
$routingCli = [IO.Path]::GetFullPath("$PSScriptRoot\..\..\NServiceBus.Community.Routing.Cli\bin\Debug\net10.0\NServiceBus.Community.Routing.Cli.dll")
function routing { dotnet $routingCli @args }

Write-Host "routing calls the router at http://localhost:5080, e.g. routing status"
