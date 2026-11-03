using Microsoft.Extensions.Hosting;

//The warehouse of branch B. Shipping at HQ sends ShipOrder for the orders of branch B to it.
await BranchEndpoint.CreateBuilder(args, "Warehouse", Branch.B).Build().RunAsync();
