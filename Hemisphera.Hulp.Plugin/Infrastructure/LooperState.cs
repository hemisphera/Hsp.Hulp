using System.Diagnostics;
using Hemisphera.Hulp.Plugin.Models;
using Hemisphera.Hulp.Plugin.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReaSharp;
using ReaSharp.Models;

namespace Hemisphera.Hulp.Plugin.Infrastructure;

/// <summary>
/// Song detection and the record-then-loop logic.
/// </summary>
/// <remarks>
/// Everything here runs in <see cref="HulpScheduler"/> passes (on REAPER's main thread in safe mode): <see cref="Tick"/>
/// is driven by <see cref="HulpScheduler.Tick"/>, commands are dispatched to the scheduler, and <c>await</c>s resume
/// there.
/// </remarks>
public class LooperState : ObservedEntity
{
  private readonly ILogger<LooperState> _logger;
  private readonly IOptionsMonitor<LooperSettings> _settings;
  private readonly HulpMonitor _monitor;
  private readonly HulpScheduler _scheduler;
  private readonly Transport _transport;
  private double _lookaheadBeats;

  private TrackSelector? _lastSelector;
  private Stopwatch? _timer;
  private bool _running;

  public TimeSpan LookAhead
  {
    get
    {
      var bpm = Reaper.TimeMap2_GetDividedBpmAtTime.Invoke(0, _transport.PlayheadOrCursorPosition.TotalSeconds);
      return TimeSpan.FromSeconds(60.0 / bpm * _lookaheadBeats);
    }
  }

  public TrackArea? ActiveArea
  {
    get => field;
    private set => SetFieldValue(ref field, value);
  }

  public TrackArea? UpcomingArea
  {
    get => field;
    private set => SetFieldValue(ref field, value);
  }

  public Song? CurrentSong { get; private set; }


  public LooperState(
    ILogger<LooperState> logger,
    IOptionsMonitor<LooperSettings> settings,
    HulpMonitor monitor,
    HulpScheduler scheduler
  )
  {
    _logger = logger;
    _settings = settings;
    _monitor = monitor;
    _scheduler = scheduler;
    _transport = new Transport(Project.Default);

    PropertyChanged += (_, args) =>
    {
      if (args.PropertyName == nameof(UpcomingArea))
      {
        HulpScheduler.Forget(UpcomingAreaChanged(), _logger);
      }

      if (args.PropertyName == nameof(ActiveArea))
      {
        HulpScheduler.Forget(ActiveAreaChanged(args.OldValue as TrackArea, args.NewValue as TrackArea), _logger);
      }
    };
    _scheduler.Tick += Tick;
    _logger.LogDebug("LooperState loaded");
  }


  public async Task Start()
  {
    await Initialize();
    _running = true;
    _transport.Play();
  }

  public void Stop()
  {
    _running = false;
    _timer?.Stop();
    CurrentSong?.Dispose();
    CurrentSong = null;
    _lastSelector = null;

    DisablePropertyNotifications += 1;
    ActiveArea = null;
    UpcomingArea = null;
    DisablePropertyNotifications -= 1;
  }

  public async Task Initialize(TimeSpan? time = null)
  {
    try
    {
      Reaper.PreventUIRefresh.Invoke(1);
      Stop();

      if (time != null)
      {
        _transport.CursorPosition = time.Value;
      }

      _lookaheadBeats = _settings.CurrentValue.LookaheadBeats;
      _logger.LogDebug("Using lookahead of {beats} beats.", _lookaheadBeats);
      _logger.LogDebug("Using {containerTrackName} as container track.", _settings.CurrentValue.ContainerTrackName);
      _transport.Update();
      var now = _transport.PlayheadOrCursorPosition;
      var songs = EnumerateSongs();
      foreach (var song in songs)
      {
        song.SetActive(now.IsWithin(song.Region));
      }

      CurrentSong = songs.FirstOrDefault(s => now.IsWithin(s.Region));
      CurrentSong?.Initialize(now);
      _monitor.LoadSong(CurrentSong, songs);

      _timer = Stopwatch.StartNew();
    }
    finally
    {
      Reaper.PreventUIRefresh.Invoke(-1);
      Reaper.UpdateArrange.Invoke();
      Reaper.TrackList_AdjustWindows.Invoke(false);
    }
  }

  public List<Song> EnumerateSongs()
  {
    var trackTree = TrackTreeItem.Build(Project.Default);
    var songs = Region.Enumerate(Project.Default)
      .Select(r => Song.FromRegion(r, trackTree, _settings.CurrentValue.ContainerTrackName))
      .OfType<Song>()
      .ToList();
    return songs;
  }

  public async Task FocusSong(TimeSpan? time = null)
  {
    await Initialize(time);
    if (CurrentSong == null) return;
    _transport.Project.SetSelection(CurrentSong.Region);

    Reaper.Main_OnCommandEx.Invoke(40031, 0, _transport.Project.ReaperHandle); // Zoom to time selection

    _transport.Project.ClearSelection();
    _transport.CursorPosition = CurrentSong.Region.Start;
  }


  private void Tick()
  {
    if (!_running || CurrentSong == null) return;

    _transport.Update();

    if (_transport.PlayheadPosition > CurrentSong.Region.End && _transport.IsPlaying)
    {
      Stop();
      Reaper.Main_OnCommandEx.Invoke(40328, 0, _transport.Project.ReaperHandle);
      _logger.LogDebug("Reached end of region");
      return;
    }

    if (!_transport.IsPlaying && _timer?.Elapsed.TotalSeconds > 3)
    {
      Stop();
      _logger.LogDebug("Playback stopped. Looper reset. ");
      return;
    }

    var pos = _transport.PlayheadOrCursorPosition;
    HandleTrackSelector(pos);
    ActiveArea = CurrentSong.RecordingAreas.FirstOrDefault(a => pos.IsWithin(a.Item));
    UpcomingArea = CurrentSong.RecordingAreas.FirstOrDefault(a => (pos + LookAhead).IsWithin(a.Item));
  }


  private async Task UpcomingAreaChanged()
  {
    // do nothing unless we're playing
    if (!_transport.IsPlaying) return;

    _logger.LogDebug("Upcoming area changed: {newArea}", UpcomingArea);

    if (UpcomingArea?.State == AreaState.Clean)
    {
      await UpcomingArea.BeginRecording();
      if (!_transport.IsRecording)
      {
        _transport.ToggleRecordAtNextBeat();
      }
    }

    // if recording and no more upcoming item, stop recording.
    if (UpcomingArea == null && _transport.IsRecording)
    {
      _transport.ToggleRecordAtNextBeat();
    }
  }

  private async Task ActiveAreaChanged(TrackArea? oldArea, TrackArea? newArea)
  {
    if (!_transport.IsPlaying) return;

    _logger.LogDebug("Active area changed: {newArea}", newArea);

    if (oldArea is { State: AreaState.Recording })
    {
      HulpScheduler.Forget(oldArea.FinalizeRecording(_transport, _logger), _logger);
    }

    if (newArea?.SourceType == SourceType.Midi)
    {
      // perform initial propagate after 1/4 of area has passed for MIDI items
      var song = CurrentSong;
      await Task.Delay(TimeSpan.FromSeconds(newArea.Item.Length.TotalSeconds / 4));

      // The looper may have been stopped or the song changed (disposing its areas) in the meantime.
      if (!_running || CurrentSong != song) return;
      await newArea.PropageToLoopAreas(_transport, _logger);
    }
  }

  private void HandleTrackSelector(TimeSpan pos)
  {
    var selector = CurrentSong?.Selectors
      .Where(s => s.Start <= pos)
      .OrderBy(s => s.Start)
      .LastOrDefault();
    if (_lastSelector == selector) return;
    if (selector == null) return;
    _lastSelector = selector;
    _logger.LogDebug("Selector activated: {selector}", selector);
    selector.Track.SelectExclusive();
  }

  public async Task ChangeSong(int delta)
  {
    var songs = EnumerateSongs();
    if (songs.Count == 0) return;

    var currSongRegionId = CurrentSong?.Region.Id ?? -1;
    var idx = songs.FindIndex(a => a.Region.Id == currSongRegionId);

    var newIdx = Math.Clamp(idx + delta, 0, songs.Count - 1);
    _logger.LogDebug("Selecting {song}...", newIdx);
    await FocusSong(songs[newIdx].Region.Start);
  }
}
