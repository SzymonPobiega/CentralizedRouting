using NServiceBus;

namespace Messages
{
    /// <summary>
    /// Sent by Website to Sales.
    /// </summary>
    public class PlaceOrder : ICommand
    {
        public string OrderId { get; set; }
    }

    /// <summary>
    /// Sales' reply to Website.
    /// </summary>
    public class PlaceOrderResponse : IMessage
    {
        public string OrderId { get; set; }
    }

    /// <summary>
    /// Published by Sales once it accepts an order.
    /// </summary>
    public class OrderPlaced : IEvent
    {
        public string OrderId { get; set; }
    }

    /// <summary>
    /// Published by Billing once it charged the order.
    /// </summary>
    public class OrderBilled : IEvent
    {
        public string OrderId { get; set; }
    }

    /// <summary>
    /// Published by Shipping once it shipped the order.
    /// </summary>
    public class OrderShipped : IEvent
    {
        public string OrderId { get; set; }
    }
}
