# Defines the routing command line functions for this sample. Dot-source it: . .\routing.ps1
$routingCli = [IO.Path]::GetFullPath("$PSScriptRoot\..\..\CentralRouting.Cli\bin\Debug\net10.0\CentralRouting.Cli.dll")
function routing { dotnet $routingCli @args }

Write-Host "routing calls the router at http://localhost:5080, e.g. routing status"
