using Microsoft.Extensions.Hosting;

//Billing is on the billing site. It charges through PaymentGateway on the same site and publishes OrderBilled to Shipping through the router.
await ServiceEndpoint.CreateBuilder(args, "Billing", Sites.Billing, BillingService.Configure).Build().RunAsync();
