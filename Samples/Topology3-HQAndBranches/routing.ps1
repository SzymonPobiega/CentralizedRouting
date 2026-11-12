# Defines the routing command line functions for this sample. Dot-source it: . .\routing.ps1
$routingCli = [IO.Path]::GetFullPath("$PSScriptRoot\..\..\NServiceBus.Community.Routing.Cli\bin\Debug\net10.0\NServiceBus.Community.Routing.Cli.dll")
function routing { dotnet $routingCli @args }
function hq      { routing @args --router http://localhost:5090 }
function branchA { routing @args --router http://localhost:5091 }
function branchB { routing @args --router http://localhost:5092 }

Write-Host "hq, branchA and branchB call the routers at http://localhost:5090, 5091 and 5092, e.g. hq status"
