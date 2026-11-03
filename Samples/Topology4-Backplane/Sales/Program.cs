using Microsoft.Extensions.Hosting;

//Sales is in the sales part. SalesRouter forwards OrderPlaced over the backplane to BillingRouter, which holds a grant for it.
await PartEndpoint.CreateBuilder(args, "Sales", Parts.Sales, SalesService.Configure).Build().RunAsync();
