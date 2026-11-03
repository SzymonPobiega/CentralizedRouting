using NServiceBus;

namespace CentralRouting.AcceptanceTests
{
    public class PlaceOrder : ICommand
    {
        public string OrderId { get; set; }
    }

    public class PlaceOrderResponse : IMessage
    {
        public string OrderId { get; set; }
    }

    public class ChargeCard : ICommand
    {
        public string OrderId { get; set; }
    }

    public class OrderPlaced : IEvent
    {
        public string OrderId { get; set; }
    }

    public class AdjustStock : ICommand
    {
        public string Sku { get; set; }
    }

    public class StockAdjusted : IMessage
    {
        public string Sku { get; set; }
    }

    public class PriceListChanged : IEvent
    {
        public string Version { get; set; }
    }
}
