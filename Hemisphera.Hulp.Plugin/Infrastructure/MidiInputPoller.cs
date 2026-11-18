using ReaSharp;
using ReaSharp.Models;

namespace Hemisphera.Hulp.Plugin.Infrastructure;

/// <summary>
/// Polls REAPER's recent MIDI input and raises <see cref="MidiReceived"/> for each new event, oldest first.
/// </summary>
/// <remarks>
/// Replaces ReaSharp's <c>MidiListener</c>, which raises events newest first (so the last value applied for a turned
/// knob was the oldest one) and replays input received before the plugin started.
/// </remarks>
public sealed class MidiInputPoller : IDisposable
{
  private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

  private readonly CancellationTokenSource _cts = new();
  private int? _lastSequence;
  private bool _primed;

  public event EventHandler<MidiEvent>? MidiReceived;


  public MidiInputPoller()
  {
    var token = _cts.Token;
    _ = Task.Run(async () =>
    {
      while (!token.IsCancellationRequested)
      {
        try
        {
          Poll();
        }
        catch
        {
          // keep polling
        }

        await Task.Delay(PollInterval, token);
      }
    }, token);
  }


  public void Poll()
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
    _cts.Cancel();
    _cts.Dispose();
  }
}
