# Defines the routing command line functions for this sample. Dot-source it: . .\routing.ps1
$routingCli = [IO.Path]::GetFullPath("$PSScriptRoot\..\..\NServiceBus.Community.Routing.Cli\bin\Debug\net10.0\NServiceBus.Community.Routing.Cli.dll")
function routing { dotnet $routingCli @args }
function sales    { routing @args --router http://localhost:5081 }
function billing  { routing @args --router http://localhost:5082 }
function shipping { routing @args --router http://localhost:5083 }

Write-Host "sales, billing and shipping call the routers at http://localhost:5081, 5082 and 5083, e.g. billing peers list"
