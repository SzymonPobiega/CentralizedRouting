using Microsoft.Extensions.Hosting;

//Sales is on the sales site. OrderPlaced goes to Billing and OrderShipped comes from Shipping, both through the router.
await ServiceEndpoint.CreateBuilder(args, "Sales", Sites.Sales, SalesService.Configure).Build().RunAsync();
