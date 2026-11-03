//Website is on the sales site. PlaceOrder goes to Sales on the same site.
await WebsiteService.Run(ServiceEndpoint.CreateBuilder(args, "Website", Sites.Sales).Build());
