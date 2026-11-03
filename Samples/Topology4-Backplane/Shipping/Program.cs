using Microsoft.Extensions.Hosting;

//Shipping is in the shipping part, next to ParcelDelivery.
await PartEndpoint.CreateBuilder(args, "Shipping", Parts.Shipping, ShippingService.Configure).Build().RunAsync();
