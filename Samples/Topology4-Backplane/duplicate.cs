#:package Azure.Messaging.ServiceBus@7.21.0

// Simulates the backplane delivering a message twice: takes the first message waiting in a queue,
// sends an exact copy back to the same queue and leaves the original there.
using Azure.Messaging.ServiceBus;

var queue = args[0];
await using var client = new ServiceBusClient(Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString"));
await using var receiver = client.CreateReceiver(queue);
var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
if (message == null)
{
    Console.WriteLine($"No message in {queue}");
    return 1;
}

await using var sender = client.CreateSender(queue);
await sender.SendMessageAsync(new ServiceBusMessage(message));
await receiver.AbandonMessageAsync(message);
Console.WriteLine($"Duplicated {message.ApplicationProperties["NServiceBus.MessageId"]} ({message.ApplicationProperties["NServiceBus.EnclosedMessageTypes"]}) in {queue}");
return 0;
