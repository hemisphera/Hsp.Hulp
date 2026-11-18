namespace Hemisphera.Hulp.WebApp.Models;

public class TransportState
{
  public bool IsPlaying { get; set; }
  public bool IsPaused { get; set; }
  public bool IsRecording { get; set; }
  /// <summary>Playhead/cursor position in quarter notes (<c>/hulp/qnpos</c>).</summary>
  public double Position { get; set; }

  /// <summary>Playhead/cursor position in seconds (<c>/hulp/time</c>).</summary>
  public double TimePosition { get; set; }

  /// <summary>Song region start/end in seconds.</summary>
  public double RegionStart { get; set; }
  public double RegionEnd { get; set; }
  public int SongId { get; set; }
  public string SongName { get; set; } = string.Empty;

  public double Remaining => RegionEnd > 0 ? Math.Max(0, RegionEnd - TimePosition) : 0;
}