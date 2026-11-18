using Microsoft.Extensions.Logging;
using ReaSharp.Models;
using ReaSharp.RppXml;

namespace Hemisphera.Hulp.Plugin.Models;

public class MonitoredParameterFactory
{
  private readonly Track _track;
  private readonly ILogger _logger;
  private readonly int? _relativatorIndex;
  private readonly RppNode[] _allFx;


  public MonitoredParameterFactory(Track track, ILogger logger)
  {
    _track = track;
    _logger = logger;
    RppReader.TryRead(track.GetTrackStateChunk() ?? string.Empty, out var trackNode);
    _relativatorIndex = FindRelativatorIndex(trackNode);
    _allFx = trackNode?.FindChild("FXCHAIN")?.FindChildren("FX").ToArray() ?? [];
  }


  public MonitoredParameter[] Build()
  {
    _logger.LogDebug("Relativator index: {relativatorIndex}", _relativatorIndex);
    _logger.LogDebug("Total FX on track: {count}", _allFx.Length);

    return _allFx
      .SelectMany(fx =>
      {
        var envs = fx.FindChildren("PROGRAMENV").ToArray();
        _logger.LogDebug("Envelopes for FX {name}: {count}", fx.Name, envs.Length);
        return envs;
      })
      .Select(TryBuild)
      .OfType<MonitoredParameter>()
      .OrderBy(p => p.Index)
      .ToArray();
  }


  private MonitoredParameter? TryBuild(RppNode envelope)
  {
    // One malformed or unexpected envelope must not prevent the other parameters from loading.
    try
    {
      if (_relativatorIndex == null) return null;
      var plink = envelope.GetPropertyValue("PLINK", 1)?.AsString().Split(':') ?? [];
      int? linkSourceFxIndex = plink.Length > 0 && int.TryParse(plink[0], out var parsedSource) ? parsedSource : null;
      _logger.LogDebug("Source FX index: {linkSourceFxIndex}", linkSourceFxIndex);
      var index = envelope.GetPropertyValue("PLINK", 2)?.AsInt32();
      _logger.LogDebug("Target FX index: {index}", index);
      if (index == null) return null;
      if (linkSourceFxIndex != _relativatorIndex) return null;

      if (envelope.DefaultValues.Count == 0) return null;
      if (!int.TryParse(envelope.DefaultValues[0].AsString().Split(':').First(), out var targetParameterIndex)) return null;

      var targetFxIndex = _allFx.IndexOf(envelope.Parent);
      if (targetFxIndex < 0) return null;

      var source = new FxParameterIndices(_track, _relativatorIndex.Value, index.Value);
      var target = new FxParameterIndices(_track, targetFxIndex, targetParameterIndex);
      return new MonitoredParameter(source, target);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Skipping FX parameter link that could not be read: {message}", ex.Message);
      return null;
    }
  }

  private static int? FindRelativatorIndex(RppNode? trackNode)
  {
    if (trackNode == null) return null;
    var allFx = trackNode.FindChild("FXCHAIN")?.FindChildren("FX").ToArray() ?? [];
    for (var i = 0; i < allFx.Length; i++)
    {
      var fx = allFx[i];
      var plugin = fx.Children.FirstOrDefault();
      var strvalue = plugin?.DefaultValues.Count >= 1 ? plugin.DefaultValues[0].AsString() : string.Empty;
      if (strvalue.Equals("Hemisphera/Relativator", StringComparison.OrdinalIgnoreCase))
      {
        return i;
      }
    }

    return null;
  }
}