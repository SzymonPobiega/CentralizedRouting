# Assigns the routes once all endpoints have reported. Publishers are assigned automatically from their declarations.
#
#   PlaceOrder    Client -> Sales                    Booth router
#   OrderPlaced   Sales -> Billing, Shipping         requested by the Cloud router from the Booth router
#   OrderBilled   Billing -> Shipping                Cloud router
#                 Billing -> Sales                   requested by the Booth router from the Cloud router
#   OrderShipped  Shipping -> Sales                  requested by the Booth router from the Cloud router
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\routing.ps1"

booth routes set-destination Rehearsal.PlaceOrder BoothSales

cloud subscriptions request Rehearsal.OrderPlaced --wait
cloud routes subscribe Rehearsal.OrderPlaced BoothBilling
cloud routes subscribe Rehearsal.OrderPlaced BoothShipping
cloud routes subscribe Rehearsal.OrderBilled BoothShipping

booth subscriptions request Rehearsal.OrderBilled --wait
booth subscriptions request Rehearsal.OrderShipped --wait
booth routes subscribe Rehearsal.OrderBilled BoothSales
booth routes subscribe Rehearsal.OrderShipped BoothSales
