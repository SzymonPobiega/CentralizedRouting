using Microsoft.Extensions.Hosting;

//ParcelDelivery is on the shipping site, next to Shipping.
await ServiceEndpoint.CreateBuilder(args, "ParcelDelivery", Sites.Shipping).Build().RunAsync();
