//Website is in the sales part. PlaceOrder goes to Sales in the same part.
await WebsiteService.Run(PartEndpoint.CreateBuilder(args, "Website", Parts.Sales).Build());
