using System.Collections.Concurrent;
using System.Threading.Channels;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Infrastructure.Messaging;

public sealed class InMemoryMessageQueue : IMessageQueue
{
    private readonly Channel<QueuedMessage> _channel = Channel.CreateUnbounded<QueuedMessage>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public int Count => _channel.Reader.Count;

    public async ValueTask EnqueueAsync(QueuedMessage message, CancellationToken cancellationToken = default)
    {
        await _channel.Writer.WriteAsync(message, cancellationToken);
    }

    public async ValueTask<QueuedMessage> DequeueAsync(CancellationToken cancellationToken = default)
    {
        return await _channel.Reader.ReadAsync(cancellationToken);
    }
}
