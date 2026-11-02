namespace Fixia.Payments.Application.Common.Interfaces;

public interface IEventPublisher
{
    Task PublishAsync<T>(string topic, string eventType, T payload, CancellationToken ct = default);
}

