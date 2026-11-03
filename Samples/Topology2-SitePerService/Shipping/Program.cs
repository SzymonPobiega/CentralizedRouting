using Microsoft.Extensions.Hosting;

//Shipping is on the shipping site. It registers parcels with ParcelDelivery on the same site and publishes OrderShipped to Sales through the router.
await ServiceEndpoint.CreateBuilder(args, "Shipping", Sites.Shipping, ShippingService.Configure).Build().RunAsync();
