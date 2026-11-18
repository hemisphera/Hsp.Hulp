using Hsp.Osc;

namespace Hemisphera.Hulp.Plugin.Infrastructure;

public interface IOscWriter
{
  /// <summary>Queues a message for sending. Never blocks; drops the oldest queued message when the queue is full.</summary>
  void Write(IMessage message);
}
