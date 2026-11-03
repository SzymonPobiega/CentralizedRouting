using Microsoft.Extensions.Hosting;

//ParcelDelivery is in the shipping part, next to Shipping.
await PartEndpoint.CreateBuilder(args, "ParcelDelivery", Parts.Shipping).Build().RunAsync();
