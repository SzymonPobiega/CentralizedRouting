using Microsoft.Extensions.Hosting;

//Billing is in the billing part, next to PaymentGateway.
await PartEndpoint.CreateBuilder(args, "Billing", Parts.Billing, BillingService.Configure).Build().RunAsync();
