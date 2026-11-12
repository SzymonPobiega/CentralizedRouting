namespace Rehearsal;

public class PlaceOrder : ICommand
{
    public string OrderId { get; set; } = "";
}

public class OrderPlaced : IEvent
{
    public string OrderId { get; set; } = "";
}

public class OrderBilled : IEvent
{
    public string OrderId { get; set; } = "";
}

public class OrderShipped : IEvent
{
    public string OrderId { get; set; } = "";
}
