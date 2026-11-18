using System.Net;
using System.Threading.Channels;
using Hsp.Osc;
using Microsoft.Extensions.Logging;
using ReaSharp;

namespace Hemisphera.Hulp.Plugin.Infrastructure;

public class OscTransport : ITransport
{
  private readonly ILogger<OscTransport> _logger;
  private readonly ChannelReader<IMessage> _reader;
  private OscUdpClient? _client;
  private CancellationTokenSource? _sendCts;
  private Task? _sendLoop;
  private readonly SemaphoreSlim _gate = new(1, 1);

  public event EventHandler? Connected;


  public OscTransport(ILogger<OscTransport> logger, ChannelReader<IMessage> reader)
  {
    _logger = logger;
    _reader = reader;
  }


  /// <summary>
  /// (Re)connects to the "Hulp" OSC device. Call from a scheduler pass (reads REAPER settings).
  /// </summary>
  public async Task StartAsync(CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    try
    {
      await StopCoreAsync();
      await StartCoreAsync(ct);
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task StopAsync(CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    try
    {
      await StopCoreAsync();
    }
    finally
    {
      _gate.Release();
    }
  }

  private async Task StartCoreAsync(CancellationToken ct)
  {
    _logger.LogInformation("Connecting ...");
    var client = CreateClient();
    if (client == null) return;
    await client.ConnectAsync();
    _client = client;

    // Anything queued while disconnected is stale; subscribers resend the full state on Connected.
    while (_reader.TryRead(out _))
    {
    }

    _sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var token = _sendCts.Token;
    _sendLoop = Task.Run(() => SendLoop(client, token), CancellationToken.None);
    _logger.LogInformation("Connected");

    Connected?.Invoke(this, EventArgs.Empty);
  }

  private async Task SendLoop(OscUdpClient client, CancellationToken token)
  {
    var failing = false;
    try
    {
      await foreach (var item in _reader.ReadAllAsync(token))
      {
        try
        {
          await client.SendMessageAsync(item);
          if (failing)
          {
            failing = false;
            _logger.LogInformation("Sending OSC to {ip}:{port} works again.", client.Address, client.Port);
          }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
          // A single failed send (e.g. "connection refused" while the WebApp is not running) must not stop the
          // transport. Log the first failure of a series only.
          if (!failing)
          {
            failing = true;
            _logger.LogWarning("Failed to send OSC to {ip}:{port}: {message}", client.Address, client.Port, ex.Message);
          }
        }
      }
    }
    catch (OperationCanceledException)
    {
      // stopping
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "OSC send loop exited unexpectedly: {message}", ex.Message);
    }
  }

  private OscUdpClient? CreateClient()
  {
    try
    {
      var ini = ReaperGlobal.ReadSettings();
      if (ini == null) return null;

      var oscDevice = ReaperGlobal.EnumerateOscDevices()
        .FirstOrDefault(dev => dev.Name.Equals("Hulp", StringComparison.OrdinalIgnoreCase));
      if (oscDevice == null)
      {
        _logger.LogWarning("No OSC device 'Hulp' found");
        return null;
      }

      if (!IPAddress.TryParse(oscDevice.DeviceIp, out var address))
      {
        _logger.LogWarning("OSC device 'Hulp' has an invalid device IP '{ip}'", oscDevice.DeviceIp);
        return null;
      }

      var client = new OscUdpClient(address, oscDevice.DevicePort);
      _logger.LogInformation("Created OSC transport to {ip}:{port}", client.Address, client.Port);
      return client;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to create OSC transport: {message}", ex.Message);
      return null;
    }
  }

  private async Task StopCoreAsync()
  {
    if (_sendCts == null && _client == null) return;

    _logger.LogInformation("Disconnecting ...");
    if (_sendCts != null)
    {
      await _sendCts.CancelAsync();
      if (_sendLoop != null)
        await _sendLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      _sendCts.Dispose();
      _sendCts = null;
      _sendLoop = null;
    }

    if (_client != null)
    {
      await _client.DisconnectAsync();
      _client.Dispose();
      _client = null;
    }

    _logger.LogInformation("Disconnected");
  }
}
