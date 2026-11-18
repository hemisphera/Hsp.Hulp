using System.Threading.Channels;
using Hsp.Osc;

namespace Hemisphera.Hulp.Plugin.Infrastructure;

public class ChannelOscWriter : IOscWriter
{
  private readonly ChannelWriter<IMessage> _writer;


  public ChannelOscWriter(ChannelWriter<IMessage> writer)
  {
    _writer = writer;
  }


  public void Write(IMessage message)
  {
    // The channel is created with BoundedChannelFullMode.DropOldest, so TryWrite always succeeds and never blocks.
    _writer.TryWrite(message);
  }
}
