using System.Runtime.InteropServices;
using System.Threading.Channels;
using Hemisphera.Hulp.Plugin.Devices;
using Hemisphera.Hulp.Plugin.Infrastructure;
using Hemisphera.Hulp.Plugin.Settings;
using Hsp.Osc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReaSharp;
using ReaSharp.Models;

namespace Hemisphera.Hulp.Plugin;

public static class Plugin
{
  private static HulpScheduler? _scheduler;
  private static IHost? _host;


  [UnmanagedCallersOnly(EntryPoint = "ReaperPluginEntry")]
  public static int ReaperPluginEntry(IntPtr hInstance, IntPtr rec)
  {
    // REAPER calls the entry point again with rec == NULL when unloading the plugin.
    if (rec == IntPtr.Zero)
    {
      Shutdown();
      return 0;
    }

    try
    {
      return Load(rec);
    }
    catch (Exception ex)
    {
      ReaperConsoleLogger.WriteLog("Failed to initialize Hulp plugin: " + ex.Message);
      return 0;
    }
  }

  private static int Load(IntPtr rec)
  {
    // Created first: everything that touches the REAPER API runs through it.
    var scheduler = HulpScheduler.Create();
    _scheduler = scheduler;

    var settingsPath = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
      "hulp.json");

    var host = Host.CreateDefaultBuilder()
      .ConfigureLogging((context, lb) =>
      {
        lb.ClearProviders();
        lb.AddConfiguration(context.Configuration.GetSection("Logging"));
        lb.AddProvider(new ReaperConsoleLoggerProvider());
      })
      .ConfigureServices((context, sc) =>
      {
        sc.AddSingleton(scheduler);

        // OSC messages are produced in scheduler passes and sent from a background loop. The queue never blocks the
        // producer: when nothing drains it (no "Hulp" OSC device), the oldest messages are dropped; the full state is
        // resent whenever the transport connects.
        sc.AddSingleton<Channel<IMessage>>(_ =>
          Channel.CreateBounded<IMessage>(new BoundedChannelOptions(1000)
          {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
          }));
        sc.AddSingleton<ChannelWriter<IMessage>>(sp => sp.GetRequiredService<Channel<IMessage>>().Writer);
        sc.AddSingleton<ChannelReader<IMessage>>(sp => sp.GetRequiredService<Channel<IMessage>>().Reader);
        sc.AddSingleton<IOscWriter, ChannelOscWriter>();

        if (context.Configuration.GetSection("Devices:Apc64").Exists())
        {
          sc.Configure<Apc64DeviceSettings>(context.Configuration.GetSection("Devices:Apc64"));
          sc.AddSingleton<IDevice, Apc64Device>();
        }

        sc.Configure<LooperSettings>(context.Configuration.GetSection(nameof(LooperSettings)));
        sc.Configure<EngineSettings>(context.Configuration.GetSection("Engine"));
        sc.AddSingleton<ICommandRegistry, DefaultCommandRegistry>();
        sc.AddSingleton<HulpMonitor>();
        sc.AddSingleton<OscTransport>();
        sc.AddSingleton<LooperState>();
        sc.AddSingleton<MidiInputPoller>();
        sc.AddSingleton<ITransport, OscTransport>(services => services.GetRequiredService<OscTransport>());
      })
      .ConfigureAppConfiguration(cfg => { cfg.AddJsonFile(settingsPath, optional: true, reloadOnChange: true); })
      .Build();
    _host = host;

    var state = PluginState.Initialize(ReaperPluginInfo.FromPointer(rec), host);
    scheduler.SetLogger(state.Services.GetRequiredService<ILogger<HulpScheduler>>());

    var commands = state.Services.GetRequiredService<ICommandRegistry>();
    RegisterCommand(commands, scheduler, "HULP_START", "Hulp: Start", Commands.StartLooper);
    RegisterCommand(commands, scheduler, "HULP_DEBUG", "Hulp: Print Debug Info", Commands.DumpDebug);
    RegisterCommand(commands, scheduler, "HULP_FOCUS", "Hulp: Focus song", Commands.FocusCurrentSong);
    RegisterCommand(commands, scheduler, "HULP_INIT", "Hulp: Initialize", Commands.Initialize);
    RegisterCommand(commands, scheduler, "HULP_SELECT", "Hulp: Select Song", Commands.SelectSong);
    RegisterCommand(commands, scheduler, "HULP_RESTART", "Hulp: Restart Engine", Commands.RestartEngine);
    RegisterCommand(commands, scheduler, "HULP_SELECT_NEXT", "Hulp: Select Next Song", Commands.SelectNextSong);
    RegisterCommand(commands, scheduler, "HULP_SELECT_PREVIOUS", "Hulp: Select Previous Song", Commands.SelectPreviousSong);
    RegisterCommand(commands, scheduler, "HULP_RECONNECT_DEVICES", "Hulp: Reconnect devices", Commands.Reconnect);

    // Deliberately not routed through the scheduler: it must work even if the scheduler is stuck.
    commands.Register("HULP_TOGGLE_SAFE_MODE", "Hulp: Toggle safe mode", Commands.ToggleSafeMode);

    var monitor = state.Services.GetRequiredService<HulpMonitor>();
    monitor.Start();

    // Resolve the looper now so that it is attached to the scheduler tick.
    state.Services.GetRequiredService<LooperState>();

    var transport = state.Services.GetRequiredService<ITransport>();
    transport.Connected += (_, _) => monitor.FullRefresh();
    var logger = state.Services.GetRequiredService<ILogger<OscTransport>>();
    scheduler.Post(() => HulpScheduler.Forget(transport.StartAsync(CancellationToken.None), logger));

    // Start running passes only once everything is wired up. Safe mode (main thread) vs. fast mode (background
    // thread) is applied live when hulp.json changes.
    var engineSettings = state.Services.GetRequiredService<IOptionsMonitor<EngineSettings>>();
    scheduler.Start(engineSettings.CurrentValue.SafeMode, TimeSpan.FromMilliseconds(engineSettings.CurrentValue.FastModeIntervalMs));
    engineSettings.OnChange(settings => scheduler.SetMode(settings.SafeMode, TimeSpan.FromMilliseconds(settings.FastModeIntervalMs)));

    return 1;
  }

  /// <summary>
  /// Registers a command whose handler runs in a scheduler pass (on REAPER's main thread in safe mode). ReaSharp
  /// itself invokes command handlers on a thread-pool thread.
  /// </summary>
  private static void RegisterCommand(
    ICommandRegistry commands,
    HulpScheduler scheduler,
    string id,
    string name,
    Func<IServiceProvider, ActionContext, Task> handler)
  {
    commands.Register(id, name, (sp, ctx) => scheduler.InvokeAsync(() => handler(sp, ctx)));
  }

  private static void Shutdown()
  {
    try
    {
      _scheduler?.Stop();
      var host = _host;
      if (host == null) return;
      // On a pool thread: blocking here on work that awaits back onto the (no longer running) scheduler would hang.
      Task.Run(async () =>
      {
        await host.StopAsync(TimeSpan.FromSeconds(1));
        host.Dispose();
      }).Wait(TimeSpan.FromSeconds(2));
    }
    catch
    {
      // REAPER is shutting down; nothing sensible left to do.
    }
  }
}
