using NServiceBus;

namespace Messages
{
    /// <summary>
    /// Sent by Website to Sales, for the branch the customer picked.
    /// </summary>
    public class PlaceOrder : ICommand
    {
        public string OrderId { get; set; }
        public string Branch { get; set; }
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
        public string Branch { get; set; }
    }

    /// <summary>
    /// Published by Billing once it charged the order.
    /// </summary>
    public class OrderBilled : IEvent
    {
        public string OrderId { get; set; }
        public string Branch { get; set; }
    }

    /// <summary>
    /// Sent by Shipping to the Warehouse of the order's branch.
    /// </summary>
    public class ShipOrder : ICommand
    {
        public string OrderId { get; set; }
    }

    /// <summary>
    /// A Warehouse's reply to Shipping.
    /// </summary>
    public class ShipOrderResponse : IMessage
    {
        public string OrderId { get; set; }
        public string Branch { get; set; }
    }

    /// <summary>
    /// Sent by a Warehouse to the Backorder service of its own branch.
    /// </summary>
    public class Replenish : ICommand
    {
        public string OrderId { get; set; }
        public string Branch { get; set; }
    }

    /// <summary>
    /// Published by Shipping once it shipped the order.
    /// </summary>
    public class OrderShipped : IEvent
    {
        public string OrderId { get; set; }
    }
}
