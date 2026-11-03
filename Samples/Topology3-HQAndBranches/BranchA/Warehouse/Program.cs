using Microsoft.Extensions.Hosting;

//The warehouse of branch A. Shipping at HQ sends ShipOrder for the orders of branch A to it.
await BranchEndpoint.CreateBuilder(args, "Warehouse", Branch.A).Build().RunAsync();
