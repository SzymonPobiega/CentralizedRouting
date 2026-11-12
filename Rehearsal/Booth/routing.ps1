# Defines the routing command line functions for the rehearsal. Dot-source it: . .\routing.ps1
$routingCli = [IO.Path]::GetFullPath("$PSScriptRoot\..\..\CentralRouting.Cli\bin\Debug\net10.0\CentralRouting.Cli.dll")
function booth { dotnet $routingCli @args --router http://localhost:5095 }
function cloud { dotnet $routingCli @args --router http://localhost:5096 }

Write-Host "booth calls the Booth router, cloud the Cloud router, e.g. booth status"
