using System.Collections.Concurrent;

namespace CodeMemory.AspNet.Services;

public sealed record NotificationMessage(string Text, string Type);

public sealed class NotificationService
{
    readonly ConcurrentQueue<NotificationMessage> messages = new();

    public void Publish(string text)
        => PublishInfo(text);

    public void PublishInfo(string text)
        => messages.Enqueue(new NotificationMessage(text, "info"));

    public void PublishSuccess(string text)
        => messages.Enqueue(new NotificationMessage(text, "success"));

    public void PublishWarning(string text)
        => messages.Enqueue(new NotificationMessage(text, "warning"));

    public void PublishError(string text)
        => messages.Enqueue(new NotificationMessage(text, "error"));

    public NotificationMessage? TryDequeue()
        => messages.TryDequeue(out var msg) ? msg : null;
}
