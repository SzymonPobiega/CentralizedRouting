using Microsoft.Extensions.Hosting;

//The backorder service of branch B. It gets Replenish from the warehouse of the same branch.
await BranchEndpoint.CreateBuilder(args, "Backorder", Branch.B).Build().RunAsync();
