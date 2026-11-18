using Hemisphera.Hulp.Plugin.Devices;
using Hemisphera.Hulp.Plugin.Models;
using Hemisphera.Hulp.Plugin.StateModels;
using Hsp.Osc;
using Microsoft.Extensions.Logging;
using Hemisphera.Hulp.Shared;
using ReaSharp;
using ReaSharp.Models;

namespace Hemisphera.Hulp.Plugin.Infrastructure;

public class HulpMonitor
{
  private readonly ILogger<HulpMonitor> _logger;
  private readonly IOscWriter _osc;
  private readonly IDevice[] _devices;

  private readonly HulpState _state;
  private readonly ParameterState[] _parameters;
  private readonly TrackState[] _tracks;
  private readonly SongState[] _songs;
  private readonly EventState[] _events;

  private Track? _currentTrack;
  private readonly List<MonitoredParameter> _monitoredParameters = [];
  private Song? _currentSong;
  private readonly SemaphoreSlim _lock = new(1, 1);

  private CancellationTokenSource? _cancellationTokenSource;
  private string? _lastError;
  private readonly Transport _transport = new(Project.Default);


  public HulpMonitor(ILogger<HulpMonitor> logger, IOscWriter osc, IEnumerable<IDevice> devices)
  {
    _logger = logger;
    _osc = osc;
    _devices = devices.ToArray();
    foreach (var device in _devices)
    {
      device.Connect();
      device.ParameterChanged += DeviceParameterChangedHandler;
    }

    _state = new HulpState();
    _state.PropertyChanged += StatePropertyChangedCallback;
    _parameters = Enumerable.Range(0, Constants.NoOfParameters).Select(index =>
    {
      var item = new ParameterState(index);
      item.PropertyChanged += ParameterPropertyChangedCallback;
      return item;
    }).ToArray();
    _tracks = Enumerable.Range(0, Constants.NoOfTracks).Select(index =>
    {
      var item = new TrackState(index);
      item.PropertyChanged += TrackPropertyChangedCallback;
      return item;
    }).ToArray();
    _songs = Enumerable.Range(0, Constants.NoOfSongs).Select(index =>
    {
      var item = new SongState(index);
      item.PropertyChanged += SongPropertyChangedCallback;
      return item;
    }).ToArray();
    _events = Enumerable.Range(0, Constants.NoOfEvents).Select(index =>
    {
      var item = new EventState(index);
      item.PropertyChanged += EventPropertyChangedCallback;
      return item;
    }).ToArray();
  }

  private void DeviceParameterChangedHandler(object? sender, ParameterChangedEventArgs e)
  {
    var mp = _monitoredParameters.FirstOrDefault(mp => mp.Index == e.ParameterIndex);
    if (mp is null) return;
    var fxp = mp.Source.GetParameter();
    fxp.NormalizedValue = e.Value / 127.0;
  }


  private void StatePropertyChangedCallback(object? sender, PropertyValueChangedEventArgs e)
  {
    if (sender is not HulpState state) return;
    if (e.PropertyName is nameof(state.CurrentTrackName) or "")
    {
      _osc.Write(state.CurrentTrackName.ToOscMessage("/hulp/track/curr/name"));
    }

    if (e.PropertyName is nameof(state.Beat) or "")
    {
      var text = $"{state.Beat.Beat}/{state.Beat.Length}";
      _osc.Write(text.ToOscMessage("/hulp/beat"));
    }

    if (e.PropertyName is nameof(state.Region) or "")
    {
      var start = state.Region?.Start.TotalSeconds ?? 0.0;
      var end = state.Region?.End.TotalSeconds ?? 0.0;
      _osc.Write(new Message("/hulp/song/curr").PushAtom(state.Region?.Id ?? -1).PushAtom(start).PushAtom(end));
      _osc.Write(new Message("/hulp/song/curr/name").PushAtom(state.Region?.Name ?? string.Empty));
    }

    if (e.PropertyName is nameof(state.QuarterNotePosition) or "")
    {
      _osc.Write(state.QuarterNotePosition.ToOscMessage("/hulp/qnpos"));
    }

    if (e.PropertyName is nameof(state.TimePosition) or "")
    {
      _osc.Write(state.TimePosition.ToOscMessage("/hulp/time"));
    }
  }

  private void ParameterPropertyChangedCallback(object? sender, PropertyValueChangedEventArgs e)
  {
    if (sender is not ParameterState state) return;
    var baseAddress = $"/hulp/track/curr/fx/{state.Index + 1}";
    if (e.PropertyName is nameof(state.Name) or "")
      _osc.Write(state.Name.ToOscMessage(baseAddress + "/name"));
    if (e.PropertyName is nameof(state.FormattedValue) or "")
      _osc.Write(state.FormattedValue.ToOscMessage(baseAddress + "/value/str"));
    if (e.PropertyName is nameof(state.Percentage) or "")
    {
      _osc.Write(state.Percentage.ToOscMessage(baseAddress + "/value"));
      foreach (var device in _devices)
      {
        device.SetParameter(state.Index, (int)(state.Percentage * 127));
      }
    }
  }

  private void TrackPropertyChangedCallback(object? sender, PropertyValueChangedEventArgs e)
  {
    if (sender is not TrackState state) return;
    var baseAddress = $"/hulp/track/{state.Index + 1}";
    if (e.PropertyName is nameof(state.Name) or "")
      _osc.Write(state.Name.ToOscMessage(baseAddress + "/name"));
    if (e.PropertyName is nameof(state.Selected) or "")
      _osc.Write(state.Selected.ToOscMessage(baseAddress + "/sel"));
    if (e.PropertyName is nameof(state.Mute) or "")
      _osc.Write(state.Mute.ToOscMessage(baseAddress + "/mute"));
    if (e.PropertyName is nameof(state.Solo) or "")
      _osc.Write(state.Solo.ToOscMessage(baseAddress + "/solo"));
    if (e.PropertyName is nameof(state.RecordArm) or "")
      _osc.Write(state.RecordArm.ToOscMessage(baseAddress + "/recarm"));
  }

  private void SongPropertyChangedCallback(object? sender, PropertyValueChangedEventArgs e)
  {
    if (sender is not SongState item) return;
    var baseAddress = $"/hulp/song/{item.Index + 1}";
    if (e.PropertyName is nameof(item.RegionId) or "")
      _osc.Write(item.RegionId.ToOscMessage(baseAddress + "/id"));
    if (e.PropertyName is nameof(item.Name) or "")
      _osc.Write((item.Name ?? string.Empty).ToOscMessage(baseAddress + "/name"));
  }

  private void EventPropertyChangedCallback(object? sender, PropertyValueChangedEventArgs e)
  {
    if (sender is not EventState state) return;
    var baseAddress = $"/hulp/event/{state.Index + 1}";
    if (e.PropertyName is nameof(state.Text) or "")
      _osc.Write(state.Text.ToOscMessage(baseAddress + "/text"));
    if (e.PropertyName is nameof(state.Position) or "")
      _osc.Write(state.Position.ToOscMessage(baseAddress + "/pos"));
  }

  public async Task Start()
  {
    _cancellationTokenSource = new CancellationTokenSource();
    var token = _cancellationTokenSource.Token;
    _ = Task.Run(async () => { await MonitorLoop(token); }, token);
    _logger.LogDebug("Monitor started.");
    await Task.CompletedTask;
  }

  public async Task Stop()
  {
    if (_cancellationTokenSource == null) return;
    await _cancellationTokenSource.CancelAsync();
  }


  private async Task MonitorLoop(CancellationToken token)
  {
    try
    {
      while (!token.IsCancellationRequested)
      {
        await Task.Delay(50, token);
        await _lock.WaitAsync(token);
        try
        {
          OnTick();
        }
        finally
        {
          _lock.Release();
        }
      }
    }
    catch (OperationCanceledException)
    {
      // stopped
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Monitor loop exited unexpectedly: {message} {stack}.", ex.Message, ex.StackTrace);
    }
  }

  private void OnTick()
  {
    // Each step is isolated so that an error in one (e.g. an unexpected FX chain) neither stops the others nor the
    // monitor itself; it is retried on the next tick.
    Run(nameof(UpdateCurrentTrack), UpdateCurrentTrack);
    Run(nameof(UpdateTempo), UpdateTempo);
    Run(nameof(UpdateParameters), UpdateParameters);
    Run(nameof(UpdateTracks), UpdateTracks);
  }

  private void Run(string name, Action step)
  {
    try
    {
      step();
    }
    catch (Exception ex)
    {
      // Log once per distinct error instead of on every tick.
      var key = name + ex.Message;
      if (key == _lastError) return;
      _lastError = key;
      _logger.LogError(ex, "Monitor step {step} failed: {message}", name, ex.Message);
    }
  }

  private void UpdateTempo()
  {
    _transport.Update();
    var sig = _transport.GetTimeSignature();
    _state.Beat = new BeatInfo((int)(sig?.Beats ?? -1) + 1, sig?.Numerator ?? 0);
    _state.QuarterNotePosition = (int)_transport.TimeToQuarterNotes();
    _state.TimePosition = Math.Round(_transport.PlayheadOrCursorPosition.TotalSeconds, 1);
  }

  private void UpdateCurrentTrack()
  {
    var selectedTrack = Project.Default.GetSelectedTrack();
    if (selectedTrack?.ReaperHandle == _currentTrack?.ReaperHandle) return;

    _currentTrack = selectedTrack;
    _monitoredParameters.Clear();
    _state.CurrentTrackName = _currentTrack?.Name ?? string.Empty;
    if (_currentTrack == null)
    {
      _logger.LogDebug("No track selected");
    }
    else
    {
      var paramFactory = new MonitoredParameterFactory(_currentTrack, _logger);
      _monitoredParameters.AddRange(paramFactory.Build());

      if (_logger.IsEnabled(LogLevel.Debug))
      {
        _logger.LogDebug("Track '{track}' selected", _currentTrack.Name);
        foreach (var item in _monitoredParameters)
        {
          _logger.LogDebug("Loaded linked parameter: {lp}", item);
        }
      }
    }

    foreach (var device in _devices)
    {
      device.ChangeTrack(_currentTrack);
    }
  }

  private void UpdateParameters()
  {
    for (var i = 0; i < _parameters.Length; i++)
    {
      var mp = _monitoredParameters.FirstOrDefault(p => p.Index == i);
      mp?.UpdateValue();
      _parameters[i].Name = mp?.Name ?? string.Empty;
      _parameters[i].Percentage = mp?.Percentage ?? 0.0;
      _parameters[i].FormattedValue = mp?.FormattedValue ?? string.Empty;
    }
  }

  private void UpdateTracks()
  {
    for (var i = 0; i < _tracks.Length; i++)
    {
      var songTrack = _currentSong?.Tracks.TryGet(i);
      _tracks[i].Name = songTrack?.Name ?? string.Empty;
      _tracks[i].Selected = songTrack?.Selected ?? false;
      _tracks[i].Mute = songTrack?.Mute ?? false;
      _tracks[i].Solo = songTrack?.Solo != null && songTrack.Solo != TrackSoloState.NotSoloed;
      _tracks[i].RecordArm = songTrack?.RecordArm ?? false;
    }
  }

  private void LoadEvents(Transport transport)
  {
    var events = CollectEvents().ToArray();
    for (var i = 0; i < _events.Length; i++)
    {
      var hulpEvent = events.FirstOrDefault(e => e.Index == i);
      _events[i].Text = hulpEvent?.Text ?? string.Empty;
      _events[i].Position = transport.TimeToQuarterNotes(TimeSpan.FromSeconds(hulpEvent?.Time ?? 0.0));
    }
  }

  private IEnumerable<HulpEvent> CollectEvents()
  {
    var index = 0;
    foreach (var item in _currentSong?.RecordingAreas ?? [])
    {
      yield return new HulpEvent
      {
        Index = index++,
        Text = $"Record: {item.Name} {item.Item.Track.Name}",
        Time = item.Item.Start.TotalSeconds
      };
    }

    foreach (var item in _currentSong?.Selectors ?? [])
    {
      yield return new HulpEvent
      {
        Index = index++,
        Text = $"Select: {item.Track.Name}",
        Time = item.Start.TotalSeconds
      };
    }

    foreach (var item in _currentSong?.Notes ?? [])
    {
      yield return new HulpEvent
      {
        Index = index++,
        Text = $"Note: {item.Text}",
        Time = item.Position.TotalSeconds
      };
    }
  }

  public void LoadSong(Song? currentSong, IReadOnlyList<Song> songs)
  {
    try
    {
      _lock.Wait();
      _currentSong = currentSong;
      _state.Region = currentSong?.Region;
      for (var i = 0; i < _songs.Length; i++)
      {
        var song = i < songs.Count ? songs[i] : null;
        _songs[i].RegionId = song?.Region.Id ?? -1;
        _songs[i].Name = song?.Name ?? string.Empty;
      }

      LoadEvents(_transport);
      FullRefresh();
      _logger.LogDebug("Loaded song: [{song}]", currentSong?.Name ?? "<none>");
    }
    finally
    {
      _lock.Release();
    }
  }

  public void FullRefresh()
  {
    StatePropertyChangedCallback(_state, PropertyValueChangedEventArgs.Empty);
    foreach (var item in _parameters)
      ParameterPropertyChangedCallback(item, PropertyValueChangedEventArgs.Empty);
    foreach (var item in _tracks)
      TrackPropertyChangedCallback(item, PropertyValueChangedEventArgs.Empty);
    foreach (var item in _songs)
      SongPropertyChangedCallback(item, PropertyValueChangedEventArgs.Empty);
    foreach (var item in _events)
      EventPropertyChangedCallback(item, PropertyValueChangedEventArgs.Empty);
  }
}