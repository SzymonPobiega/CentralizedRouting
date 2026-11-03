using Microsoft.Extensions.Hosting;

//The backorder service of branch A. It gets Replenish from the warehouse of the same branch.
await BranchEndpoint.CreateBuilder(args, "Backorder", Branch.A).Build().RunAsync();
