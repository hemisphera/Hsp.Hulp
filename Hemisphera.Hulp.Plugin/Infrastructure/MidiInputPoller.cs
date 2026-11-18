using ReaSharp;
using ReaSharp.Models;

namespace Hemisphera.Hulp.Plugin.Infrastructure;

/// <summary>
/// Polls REAPER's recent MIDI input on every scheduler pass and raises <see cref="MidiReceived"/> for each new event,
/// oldest first.
/// </summary>
/// <remarks>
/// Replaces ReaSharp's <c>MidiListener</c>, which polls from its own thread-pool loop and raises events newest first
/// (so the last value applied for a turned knob was the oldest one).
/// </remarks>
public sealed class MidiInputPoller : IDisposable
{
  private readonly HulpScheduler _scheduler;
  private int? _lastSequence;
  private bool _primed;

  public event EventHandler<MidiEvent>? MidiReceived;


  public MidiInputPoller(HulpScheduler scheduler)
  {
    _scheduler = scheduler;
    _scheduler.Tick += Poll;
  }


  private void Poll()
  {
    // GetRecentMidiEvents returns the most recent events first.
    var events = ReaperGlobal.GetRecentMidiEvents(_lastSequence);
    if (events.Count > 0)
      _lastSequence = events.Max(e => e.Sequence);

    // The first poll only establishes the current sequence: events received before the plugin started are stale.
    if (!_primed)
    {
      _primed = true;
      return;
    }

    for (var i = events.Count - 1; i >= 0; i--)
    {
      MidiReceived?.Invoke(this, events[i]);
    }
  }

  public void Dispose()
  {
    _scheduler.Tick -= Poll;
  }
}
