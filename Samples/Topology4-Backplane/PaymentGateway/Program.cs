using Microsoft.Extensions.Hosting;

//PaymentGateway is in the billing part, next to Billing.
await PartEndpoint.CreateBuilder(args, "PaymentGateway", Parts.Billing).Build().RunAsync();
