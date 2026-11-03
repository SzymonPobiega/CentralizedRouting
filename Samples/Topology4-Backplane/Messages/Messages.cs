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

    /// <summary>
    /// Sent by Billing to PaymentGateway.
    /// </summary>
    public class Charge : ICommand
    {
        public string OrderId { get; set; }
    }

    /// <summary>
    /// PaymentGateway's reply to Billing.
    /// </summary>
    public class ChargeResponse : IMessage
    {
        public string OrderId { get; set; }
    }

    /// <summary>
    /// Sent by Shipping to ParcelDelivery.
    /// </summary>
    public class RegisterParcel : ICommand
    {
        public string OrderId { get; set; }
    }

    /// <summary>
    /// ParcelDelivery's reply to Shipping.
    /// </summary>
    public class RegisterParcelResponse : IMessage
    {
        public string OrderId { get; set; }
        public string TrackingNumber { get; set; }
    }
}
