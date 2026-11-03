using Microsoft.Extensions.Hosting;

//PaymentGateway is on the billing site, next to Billing.
await ServiceEndpoint.CreateBuilder(args, "PaymentGateway", Sites.Billing).Build().RunAsync();
