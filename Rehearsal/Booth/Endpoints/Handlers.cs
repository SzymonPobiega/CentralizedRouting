namespace Rehearsal;

static class Handlers
{
    static readonly Dictionary<string, Type[]> byRole = new()
    {
        ["sales"] = [typeof(SalesPlaceOrderHandler), typeof(SalesOrderBilledHandler), typeof(SalesOrderShippedHandler)],
        ["billing"] = [typeof(BillingOrderPlacedHandler)],
        ["shipping"] = [typeof(ShippingOrderPlacedHandler), typeof(ShippingOrderBilledHandler)],
        ["client"] = []
    };

    /// <summary>
    /// All roles share the assembly, so each endpoint excludes the other roles' handlers.
    /// </summary>
    public static Type[] NotFor(string role) => byRole.Where(r => r.Key != role).SelectMany(r => r.Value).ToArray();
}

class SalesPlaceOrderHandler : IHandleMessages<PlaceOrder>
{
    public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} received from {context.MessageHeaders.GetValueOrDefault(Headers.OriginatingEndpoint)}");
        await context.Publish(new OrderPlaced { OrderId = message.OrderId });
    }
}

class SalesOrderBilledHandler : IHandleMessages<OrderBilled>
{
    public Task Handle(OrderBilled message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} billed");
        return Task.CompletedTask;
    }
}

class SalesOrderShippedHandler : IHandleMessages<OrderShipped>
{
    public Task Handle(OrderShipped message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} shipped");
        return Task.CompletedTask;
    }
}

class BillingOrderPlacedHandler : IHandleMessages<OrderPlaced>
{
    public async Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Billing order {message.OrderId}");
        await context.Publish(new OrderBilled { OrderId = message.OrderId });
    }
}

class ShippingOrderPlacedHandler : IHandleMessages<OrderPlaced>
{
    public async Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Shipping order {message.OrderId}");
        await context.Publish(new OrderShipped { OrderId = message.OrderId });
    }
}

class ShippingOrderBilledHandler : IHandleMessages<OrderBilled>
{
    public Task Handle(OrderBilled message, IMessageHandlerContext context)
    {
        Console.WriteLine($"Order {message.OrderId} is billed, as Shipping hears");
        return Task.CompletedTask;
    }
}
