namespace AutomowerBluetooth
{
    using System;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Plugin.BLE.Abstractions;
    using Plugin.BLE.Abstractions.Contracts;
    using Plugin.BLE.Abstractions.EventArgs;

    public class BluetoothIO
    {
        private readonly IBluetoothLogger _logger;
        private readonly IAdapter _adapter;
        private CancellationTokenSource _discoverCancellationTokenSource = new CancellationTokenSource();

        private ICharacteristic _inBuf;
        private ICharacteristic _outBuf;
        private bool _closing;
        private Platform _platform = Platform.Undefined;

        public BluetoothIO(IBluetoothLE bluetoothLe, IBluetoothLogger logger, Platform platform)
        {
            _platform = platform;
            Settings = new SerialIOSettings();
            bluetoothLe.StateChanged += BluetoothLeOnStateChanged;
            _logger = logger;

            _adapter = bluetoothLe.Adapter;
            _adapter.DeviceConnectionLost += AdapterOnDeviceConnectionLost;
            _adapter.DeviceDisconnected += AdapterOnDeviceDisconnected;
            _adapter.DeviceDiscovered += Adapter_DeviceDiscovered;
            _adapter.ScanTimeout = Settings.ScanTimeout;
        }

        public BluetoothIO(IAdapter adapter, SerialIOSettings settings)
        {
            Settings = settings;
            _adapter = adapter;
            _adapter.DeviceConnectionLost += AdapterOnDeviceConnectionLost;
            _adapter.DeviceDisconnected += AdapterOnDeviceDisconnected;
            _adapter.DeviceDiscovered += Adapter_DeviceDiscovered;
            _adapter.ScanTimeout = Settings.ScanTimeout;
        }

        public event EventHandler<IDevice> DeviceDiscovered;

        public event EventHandler<byte[]> BytesReceived;

        public event EventHandler<byte[]> BytesSent;

        public event EventHandler<IDevice> Connected;

        public event EventHandler<string> ConnectionFailed;

        public event EventHandler<string> Disconnected;

        public event EventHandler<ErrorArgs> Error;

        public IDevice SelectedDevice { get; set; }

        public SerialIOSettings Settings { get; set; }

        public string ByteArrayToString(byte[] byteArray)
        {
            if (byteArray == null)
            {
                return "<Empty>";
            }

            StringBuilder hex = new StringBuilder(byteArray.Length * 2);
            foreach (byte b in byteArray)
            {
                hex.AppendFormat("{0:X2} ", b);
            }
            return hex.ToString();
        }

        public async Task Close()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            try
            {
                if (SelectedDevice != null)
                {
                    try
                    {
                        await _adapter.DisconnectDeviceAsync(SelectedDevice);
                        if (_inBuf != null)
                        {
                            _inBuf.ValueUpdated -= ReadWrite_ValueUpdated;
                            _inBuf = null;
                        }
                        _outBuf = null;
                        SelectedDevice = null;
                        OnDisconnected("Device disconnected");
                    }
                    catch (Exception ex)
                    {
                        OnError("Failed to close serial IO", ex.Message);
                    }
                }
                else
                {
                    OnDisconnected("Device disconnected");
                }
            }
            finally
            {
                _closing = false;
            }
        }

        public async Task Send(byte[] message)
        {
            if (SelectedDevice?.State != DeviceState.Connected)
            {
                return;
            }

            var size = message.Length;

            var lastDataPacketSize = size % Settings.PacketSize;
            var numberOfCompletePackages = size / Settings.PacketSize;

            for (int i = 0; i < numberOfCompletePackages; i++)
            {
                var data = new ArraySegment<byte>(message, i * Settings.PacketSize, Settings.PacketSize);
                await WriteToDevice(data.ToArray());
            }
            if (lastDataPacketSize > 0)
            {
                var data = new ArraySegment<byte>(message, numberOfCompletePackages * Settings.PacketSize, lastDataPacketSize);
                await WriteToDevice(data.ToArray());
            }

            BytesSent?.Invoke(this, message);
        }

        public async Task StopDiscover()
        {
            _discoverCancellationTokenSource.Cancel();
            await _adapter.StopScanningForDevicesAsync();
        }

        public void Discover()
        {
            var services = new[] { new Guid(Settings.ServiceId) };
            //var pairedDevices = _adapter.GetSystemConnectedOrPairedDevices(services);

            SelectedDevice = null;            

            _discoverCancellationTokenSource = new CancellationTokenSource();

            Task.Run(async () =>
            {
                while (!_discoverCancellationTokenSource.IsCancellationRequested)
                {
                    if (_adapter.IsScanning)
                    {
                        await Task.Delay(500);
                    }
                    else
                    {
                        await _adapter.StartScanningForDevicesAsync(services, (device) => device.Rssi > Settings.ProximityRssi && device.Rssi < 0, false, _discoverCancellationTokenSource.Token);
                    }
                }
            }, _discoverCancellationTokenSource.Token);
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                await StopDiscover();

                await _adapter.ConnectToDeviceAsync(SelectedDevice);
                var serialIoService = await GetSerialIoService(SelectedDevice);
                var inBuf = Guid.Parse(Settings.InBufCharacteristicsId);
                var outBuf = Guid.Parse(Settings.OutBufCharacteristicsId);
                _inBuf = await serialIoService.GetCharacteristicAsync(inBuf);
                _outBuf = await serialIoService.GetCharacteristicAsync(outBuf);
                _inBuf.ValueUpdated += ReadWrite_ValueUpdated;

                switch (_platform)
                {
                    case Platform.Undefined:
                        await _inBuf.StartUpdatesAsync();
                        break;
                    case Platform.iOS:
                        try
                        {
                            await _inBuf.StartUpdatesAsync();
                        }
                        catch (Exception e)
                        {
                            // This is to be expected on iOS if pair dialog is not dismissed in approx 4 seconds.
                            await _inBuf.StartUpdatesAsync();
                        }
                        break;
                    case Platform.Android:
                        // The StartUpdatesAsync task hangs forever when we connect the first time and are paired through the OS.                    
                        var startUpdateTask = _inBuf.StartUpdatesAsync();
                        if (await Task.WhenAny(startUpdateTask, Task.Delay(2000)) != startUpdateTask)
                        {
                            await _inBuf.StopUpdatesAsync();
                            await _inBuf.StartUpdatesAsync();
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                
                Connected?.Invoke(this, SelectedDevice);
                return true;
            }
            catch (Exception e)
            {
                OnError("Connect to device failed!", e.Message);
                OnDisconnected(e.Message);
            }

            return false;
        }

        protected virtual void OnDisconnected(string e)
        {
            Disconnected?.Invoke(this, e);
        }

        protected virtual void OnDeviceDiscovered(IDevice e)
        {
            DeviceDiscovered?.Invoke(this, e);
        }

        private void BluetoothLeOnStateChanged(object sender, BluetoothStateChangedArgs bluetoothStateChangedArgs)
        {
            switch (bluetoothStateChangedArgs.NewState)
            {
                case BluetoothState.Unknown:
                    break;
                case BluetoothState.Unavailable:
                    OnError("Bluetooth error", GetStateText(bluetoothStateChangedArgs.NewState));
                    break;
                case BluetoothState.Unauthorized:
                    OnError("Bluetooth error", GetStateText(bluetoothStateChangedArgs.NewState));
                    break;
                case BluetoothState.TurningOn:
                    break;
                case BluetoothState.On:
                    break;
                case BluetoothState.TurningOff:
                    break;
                case BluetoothState.Off:
                    OnError("Bluetooth error", GetStateText(bluetoothStateChangedArgs.NewState));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private string GetStateText(BluetoothState state)
        {
            switch (state)
            {
                case BluetoothState.Unknown:
                    return "Unknown BLE state.";
                case BluetoothState.Unavailable:
                    return "BLE is not available on this device.";
                case BluetoothState.Unauthorized:
                    return "You are not allowed to use BLE.";
                case BluetoothState.TurningOn:
                    return "BLE is warming up, please wait.";
                case BluetoothState.On:
                    return "BLE is on.";
                case BluetoothState.TurningOff:
                    return "BLE is turning off. That's sad!";
                case BluetoothState.Off:
                    return "BLE is off. Turn it on!";
                default:
                    return "Unknown BLE state.";
            }
        }

        private void AdapterOnDeviceConnectionLost(object sender, DeviceErrorEventArgs deviceErrorEventArgs)
        {
            OnDisconnected("Connection to device was lost");
            OnError("Bluetooth device", "Connection to the device was lost");
        }

        private void AdapterOnDeviceDisconnected(object sender, DeviceEventArgs deviceEventArgs)
        {
            OnDisconnected("Device disconnected");
        }

        private void Adapter_DeviceDiscovered(object sender, DeviceEventArgs e)
        {
            OnDeviceDiscovered(e.Device);

            /*if (e.Device.Name == null) {
                return;
            }*/

            /*foreach (var deviceAdvertisementRecord in e.Device.AdvertisementRecords) {
                if (deviceAdvertisementRecord.Type == AdvertisementRecordType.UuidsIncomple16Bit) {
                    
                }
            }*/

            /*var found = e.Device.Rssi > Settings.ProximityRssi;
            if (found)
            {
                OnDeviceDiscovered(e.Device);
            }*/
        }        

        private void ReadWrite_ValueUpdated(object sender, CharacteristicUpdatedEventArgs e)
        {
            var value = e.Characteristic.Value;
            _logger.Debug($"BLE receive: {ByteArrayToString(value)}");
            BytesReceived?.Invoke(this, value);
        }

        private async Task WriteToDevice(byte[] message)
        {
            if (SelectedDevice?.State != DeviceState.Connected)
            {
                return;
            }

            try
            {
                _logger.Debug($"Pre BLE send: {ByteArrayToString(message)}");
                var result = await _outBuf.WriteAsync(message);
                if (!result)
                {
                    _logger.Error($"BLE send failed: {ByteArrayToString(message)}");
                }
                _logger.Debug($"Post BLE send: {ByteArrayToString(message)}");
                // BytesSent?.Invoke(this, message);
            }
            catch (Exception ex)
            {
                OnError("Failed to write to device", ex.Message);
            }
        }

        private async Task<IService> GetSerialIoService(IDevice automower)
        {
            var services = await automower.GetServicesAsync();
            var serialDeviceId = Guid.Parse(Settings.ServiceId);
            var serialIoService = services.FirstOrDefault(s => s.Id.Equals(serialDeviceId));

            if (serialIoService == null)
            {
                throw new Exception($"Failed to get serial IO service {serialDeviceId}");
            }

            return serialIoService;
        }

        private void OnError(string title, string message)
        {
            ConnectionFailed?.Invoke(this, "Failed to connect device (Bluetooth LE)");
            var e = new ErrorArgs { Message = message, Title = title };
            Error?.Invoke(this, e);
        }
    }

    public class SerialIOSettings
    {
        // Important! We switch the in and out buffer to match the meaning in our app. 
        // The spec describes these from the mowers perspective.
        
        public SerialIOSettings(
            string deviceId = "HusqvarnaMower",
            int proximityRssi = -70,
            int packetSize = 20,
            int scanTimeout = 1280,
            string serviceId = "98bd0001-0b0e-421a-84e5-ddbf75dc6de4",
            string outBufCharacteristics = "98bd0002-0b0e-421a-84e5-ddbf75dc6de4",
            string inBufCharacteristics = "98bd0003-0b0e-421a-84e5-ddbf75dc6de4",
            string protocolDescriptorCharacteristics = "98bd0004-0b0e-421a-84e5-ddbf75dc6de4")
        {
            DeviceId = deviceId;
            ProximityRssi = proximityRssi;
            ServiceId = serviceId;
            InBufCharacteristicsId = inBufCharacteristics;
            OutBufCharacteristicsId = outBufCharacteristics;
            ProtocolDescriptorCharacteristicsId = protocolDescriptorCharacteristics;
            ScanTimeout = scanTimeout;
            PacketSize = packetSize;
        }

        private SerialIOSettings()
        {
        }

        public string DeviceId { get; }

        public string ServiceId { get; }

        public int ProximityRssi { get; }

        public string InBufCharacteristicsId { get; }

        public string OutBufCharacteristicsId { get; }

        public string ProtocolDescriptorCharacteristicsId { get; }

        public int ScanTimeout { get; }

        public int PacketSize { get; }
    }

    public class ErrorArgs
    {
        public string Title { get; set; }

        public string Message { get; set; }
    }

    public enum Platform
    {
        Undefined,
        iOS,
        Android
    }
}
