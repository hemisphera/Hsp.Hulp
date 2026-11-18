using Hemisphera.Hulp.Plugin.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReaSharp;
using ReaSharp.Models;

namespace Hemisphera.Hulp.Plugin.Devices;

public class Apc64Device : IDevice
{
  private const int MidiChannel = 2;
  private const int FirstCcNumber = 14;
  private const int ParameterCount = 8;

  private readonly IOptionsMonitor<Apc64DeviceSettings> _settings;
  private readonly ILogger<Apc64Device> _logger;
  private MidiDevice? _inputDevice;
  private MidiDevice? _outputDevice;
  private readonly MidiInputPoller _midiInput;


  public Apc64Device(IOptionsMonitor<Apc64DeviceSettings> settings, ILogger<Apc64Device> logger, MidiInputPoller midiInput)
  {
    _settings = settings;
    _logger = logger;
    _midiInput = midiInput;
    _midiInput.MidiReceived += MidiInputOnMidiReceived;
  }

  private void MidiInputOnMidiReceived(object? sender, MidiEvent e)
  {
    if (_inputDevice == null) return;
    if (e.DeviceIndex != _inputDevice.Id) return;

    if (e.Channel != MidiChannel - 1) return; // MIDI channel 2
    if (e.MessageType != 11) return; // CC
    if (e.Data1 is < FirstCcNumber or >= FirstCcNumber + ParameterCount) return;
    var args = new ParameterChangedEventArgs(e.Data1 - FirstCcNumber, e.Data2);
    _logger.LogDebug("Received CC {no} value {val}", args.ParameterIndex, args.Value);
    ParameterChanged?.Invoke(this, args);
  }


  public void Connect()
  {
    var deviceName = _settings.Get(null).MidiInputDeviceName;
    _inputDevice = MidiDevice.EnumerateInputs().FirstOrDefault(d => d.Name == deviceName);
    if (_inputDevice == null)
      _logger.LogWarning("No MIDI input device found with name '{deviceName}'", deviceName);
    else
      _logger.LogDebug("Using input device {name} id {id}", _inputDevice.Name, _inputDevice.Id);

    deviceName = _settings.Get(null).MidiOutputDeviceName;
    _outputDevice = MidiDevice.EnumerateOutput().FirstOrDefault(d => d.Name == deviceName);
    if (_outputDevice == null)
      _logger.LogWarning("No MIDI output device found with name '{deviceName}'", deviceName);
    else
      _logger.LogDebug("Using output device {name} id {id}", _outputDevice.Name, _outputDevice.Id);
  }

  public void ChangeTrack(Track? currentTrack)
  {
    for (var i = 0; i < ParameterCount; i++)
    {
      SetParameter(i, 0);
    }
  }

  public void SetParameter(int index, int value)
  {
    if (_outputDevice == null) return;

    var ccNo = index + FirstCcNumber;
    _logger.LogDebug("Setting CC {ccNo} id {value}", ccNo, value);
    Reaper.StuffMIDIMessage.Invoke(
      _outputDevice.Id + 16,
      0xb0 + (MidiChannel - 1),
      ccNo,
      value);
  }

  public event EventHandler<ParameterChangedEventArgs>? ParameterChanged;
}