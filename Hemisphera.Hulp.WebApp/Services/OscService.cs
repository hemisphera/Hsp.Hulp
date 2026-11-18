using System.Net;
using Hsp.Osc;
using Hemisphera.Hulp.Shared;
using Hemisphera.Hulp.WebApp.Models;

namespace Hemisphera.Hulp.WebApp.Services;

public class OscService : BackgroundService
{
  private const int Port = 9000;
  private readonly ILogger<OscService> _logger;

  // FX Parameters
  public string CurrentTrackName
  {
    get => field;
    private set
    {
      field = value;
      FxParametersChanged?.Invoke();
    }
  } = string.Empty;

  public FxParameter[] FxParameters { get; }
  public event Action? FxParametersChanged;

  // Per-parameter value debouncers (value + value/str only; name/track update immediately).
  private readonly FxValueDebouncer[] _fxDebouncers;

  // Transport
  public TransportState Transport { get; } = new();
  public event Action? TransportChanged;

  // Tracks (Constants.NoOfTracks fixed slots, indexed from 0; OSC addresses are 1-based)
  private readonly TrackInfo[] _tracks;
  public IReadOnlyList<TrackInfo> Tracks => _tracks;
  public event Action? TracksChanged;

  // Upcoming events
  private readonly UpcomingEvent[] _events;
  private readonly Lock _eventsLock = new();

  public IReadOnlyList<UpcomingEvent> GetUpcomingEvents()
  {
    lock (_eventsLock)
    {
      var currPos = Transport.Position;
      var sortedEvents = _events
        .Where(ev => ev.Position > currPos)
        .OrderBy(ev => ev.Position)
        .ToList();
      return sortedEvents;
    }
  }

  public event Action? UpcomingEventsChanged;

  public OscService(ILogger<OscService> logger)
  {
    _logger = logger;
    _tracks = Enumerable.Range(0, Constants.NoOfTracks).Select(i => new TrackInfo(i)).ToArray();
    _events = Enumerable.Range(0, Constants.NoOfEvents).Select(_ => new UpcomingEvent()).ToArray();
    FxParameters = Enumerable.Range(0, Constants.NoOfParameters).Select(index => new FxParameter(index)).ToArray();
    _fxDebouncers = FxParameters.Select(p => new FxValueDebouncer(p, () => FxParametersChanged?.Invoke())).ToArray();
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var server = new OscUdpServer(IPAddress.Any, Port);

    server.RegisterHandler("^/hulp/track/curr/name$", ctx => { CurrentTrackName = ctx.Message.Atoms.FirstOrDefault().StringValue ?? string.Empty; });

    server.RegisterHandler(@"^/hulp/track/curr/fx/(\d+)/name$", ctx =>
    {
      if (!TryGetSlot(ctx.Match, FxParameters.Length, out var slot)) return;
      FxParameters[slot].Name = ctx.Message.Atoms.FirstOrDefault().StringValue;
      FxParametersChanged?.Invoke();
    });

    server.RegisterHandler(@"^/hulp/track/curr/fx/(\d+)/value/str$", ctx =>
    {
      if (!TryGetSlot(ctx.Match, FxParameters.Length, out var slot)) return;
      _fxDebouncers[slot].ScheduleFormatted(ctx.Message.Atoms.LastOrDefault().StringValue ?? string.Empty);
    });

    server.RegisterHandler(@"^/hulp/track/curr/fx/(\d+)/value$", ctx =>
    {
      if (!TryGetSlot(ctx.Match, FxParameters.Length, out var slot)) return;
      _fxDebouncers[slot].SchedulePercentage(ctx.Message.Atoms.FirstOrDefault().Double64Value);
    });


    server.RegisterHandler("^/play$", ctx =>
    {
      Transport.IsPlaying = ctx.Message.Atoms.FirstOrDefault().Float32Value > 0.5f;
      TransportChanged?.Invoke();
    });

    server.RegisterHandler("^/record$", ctx =>
    {
      Transport.IsRecording = ctx.Message.Atoms.FirstOrDefault().Float32Value > 0.5f;
      TransportChanged?.Invoke();
    });

    server.RegisterHandler("^/pause$", ctx =>
    {
      Transport.IsPaused = ctx.Message.Atoms.FirstOrDefault().Float32Value > 0.5f;
      TransportChanged?.Invoke();
    });

    server.RegisterHandler("^/hulp/qnpos$", ctx =>
    {
      Transport.Position = ctx.Message.Atoms.FirstOrDefault().Int32Value;
      UpdateEvents();
      TransportChanged?.Invoke();
    });

    server.RegisterHandler("^/hulp/song/curr$", ctx =>
    {
      var atoms = ctx.Message.Atoms;
      if (atoms.Count < 3) return;
      Transport.SongId = atoms[0].Int32Value;
      Transport.RegionStart = ReadNumber(atoms[1]);
      Transport.RegionEnd = ReadNumber(atoms[2]);
      TransportChanged?.Invoke();
    });

    server.RegisterHandler("^/hulp/time$", ctx =>
    {
      Transport.TimePosition = ReadNumber(ctx.Message.Atoms.FirstOrDefault());
      TransportChanged?.Invoke();
    });

    server.RegisterHandler("^/hulp/song/curr/name$", ctx =>
    {
      Transport.SongName = ctx.Message.Atoms.FirstOrDefault().StringValue ?? string.Empty;
      TransportChanged?.Invoke();
    });

    server.RegisterHandler(@"^/hulp/track/(\d+)/name$", ctx =>
    {
      if (!TryGetSlot(ctx.Match, _tracks.Length, out var index)) return;
      _tracks[index].Name = ctx.Message.Atoms.FirstOrDefault().StringValue ?? string.Empty;
      TracksChanged?.Invoke();
    });

    server.RegisterHandler(@"^/hulp/track/(\d+)/sel$", ctx =>
    {
      if (!TryGetSlot(ctx.Match, _tracks.Length, out var index)) return;
      _tracks[index].Selected = ctx.Message.Atoms.FirstOrDefault().BoolValue;
      TracksChanged?.Invoke();
    });

    server.RegisterHandler(@"^/hulp/track/(\d+)/mute$", ctx =>
    {
      if (!TryGetSlot(ctx.Match, _tracks.Length, out var index)) return;
      _tracks[index].Mute = ctx.Message.Atoms.FirstOrDefault().BoolValue;
      TracksChanged?.Invoke();
    });

    server.RegisterHandler(@"^/hulp/track/(\d+)/solo$", ctx =>
    {
      if (!TryGetSlot(ctx.Match, _tracks.Length, out var index)) return;
      _tracks[index].Solo = ctx.Message.Atoms.FirstOrDefault().BoolValue;
      TracksChanged?.Invoke();
    });

    server.RegisterHandler(@"^/hulp/track/(\d+)/recarm$", ctx =>
    {
      if (!TryGetSlot(ctx.Match, _tracks.Length, out var index)) return;
      _tracks[index].RecArm = ctx.Message.Atoms.FirstOrDefault().BoolValue;
      TracksChanged?.Invoke();
    });
    server.RegisterHandler(@"^/hulp/event/(\d+)/text$", ctx =>
    {
      if (!TryGetSlot(ctx.Match, _events.Length, out var number)) return;
      lock (_eventsLock)
      {
        _events[number].Text = ctx.Message.Atoms.FirstOrDefault().StringValue ?? string.Empty;
      }

      UpcomingEventsChanged?.Invoke();
    });
    server.RegisterHandler(@"^/hulp/event/(\d+)/pos$", ctx =>
    {
      if (!TryGetSlot(ctx.Match, _events.Length, out var number)) return;
      lock (_eventsLock)
      {
        _events[number].Position = ctx.Message.Atoms.FirstOrDefault().Double64Value;
      }

      UpcomingEventsChanged?.Invoke();
    });

    server.BeginListen();
    _logger.LogInformation("OSC service listening on port {Port}", Port);

    try
    {
      await Task.Delay(Timeout.Infinite, stoppingToken);
    }
    catch (OperationCanceledException)
    {
    }
  }

  /// <summary>Converts a 1-based slot number from an OSC address into a 0-based index, rejecting out-of-range ones.</summary>
  private static bool TryGetSlot(System.Text.RegularExpressions.Match match, int count, out int index)
  {
    index = -1;
    if (!int.TryParse(match.Groups[1].Value, out var number)) return false;
    if (number < 1 || number > count) return false;
    index = number - 1;
    return true;
  }

  private static double ReadNumber(Atom atom) => atom.TypeTag switch
  {
    TypeTag.OscDouble => atom.Double64Value,
    TypeTag.OscFloat32 => atom.Float32Value,
    TypeTag.OscInt32 => atom.Int32Value,
    _ => 0.0
  };

  private void UpdateEvents()
  {
    UpcomingEventsChanged?.Invoke();
  }

  public override void Dispose()
  {
    foreach (var d in _fxDebouncers)
      d.Dispose();
    base.Dispose();
  }
}