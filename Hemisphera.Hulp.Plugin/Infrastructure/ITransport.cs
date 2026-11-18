namespace Hemisphera.Hulp.Plugin.Infrastructure;

public interface ITransport
{
  /// <summary>Raised (on the caller's thread of <see cref="StartAsync"/>) after a connection has been established.</summary>
  event EventHandler? Connected;

  Task StartAsync(CancellationToken ct);
  Task StopAsync(CancellationToken ct);
}
