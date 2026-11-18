namespace Hemisphera.Hulp.Plugin.Settings;

public class EngineSettings
{
  /// <summary>
  /// True (default): all REAPER work runs on REAPER's main thread, about 30 times per second.
  /// False ("fast mode"): it runs on a background thread every <see cref="FastModeIntervalMs"/> milliseconds, as in
  /// earlier versions. Finer timing, but the REAPER API is called off the main thread.
  /// Applied immediately when hulp.json changes; can also be toggled with the "Hulp: Toggle safe mode" action.
  /// </summary>
  public bool SafeMode { get; set; } = true;

  /// <summary>Pass interval in fast mode (milliseconds).</summary>
  public int FastModeIntervalMs { get; set; } = 5;
}
