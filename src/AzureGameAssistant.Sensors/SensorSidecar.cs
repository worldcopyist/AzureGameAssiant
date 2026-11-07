using System.Globalization;
using System.Management;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Sensors;
using LibreHardwareMonitor.Hardware;

namespace AzureGameAssistant.Sensors;

/// <summary>
/// GPU-only LibreHardwareMonitor instance plus a best-effort ACPI thermal-zone
/// CPU temperature. CPU and motherboard LHM groups stay disabled so initialization
/// never loads WinRing0 or another kernel driver.
/// </summary>
internal sealed class SensorSidecar : IDisposable
{
    private readonly FileLogger _logger;
    private readonly UpdateVisitor _visitor = new();
    private Computer? _computer;
    private IHardware? _gpu;
    private CancellationTokenSource? _cpuStop;
    private Task? _cpuTask;
    private int _cpuTempBits = BitConverter.SingleToInt32Bits(float.NaN);
    private int _readFailures;

    public SensorSidecar(FileLogger logger)
    {
        _logger = logger;
    }

    public bool HasGpu => _gpu is not null;

    public string GpuName => _gpu?.Name ?? "—";

    public bool Open()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = false,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false,
                IsPsuEnabled = false,
            };

            _computer.Open();
            _gpu = SelectGpu(_computer);

            if (_gpu is null)
            {
                return true;
            }

            _gpu.Accept(_visitor);
            _logger.Info($"GPU 传感器已打开：{_gpu.Name}，硬件类型 {_gpu.HardwareType}");

            foreach (ISensor sensor in _gpu.Sensors)
            {
                _logger.Debug($"传感器：{sensor.SensorType} {sensor.Name} = {sensor.Value}");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("LibreHardwareMonitor 初始化失败", ex);
            DisposeComputer();
            return false;
        }
    }

    public SensorRecord Read(long qpc)
    {
        float gpuTemperature = float.NaN;
        float hotSpot = float.NaN;
        float power = float.NaN;
        float coreClock = float.NaN;
        float memoryClock = float.NaN;
        float fan = float.NaN;
        float vramUsed = float.NaN;
        float vramTotal = float.NaN;

        if (_gpu is not null)
        {
            try
            {
                _gpu.Accept(_visitor);
                gpuTemperature = FindTemperature(_gpu, includeHotSpot: false);
                hotSpot = FindTemperature(_gpu, includeHotSpot: true);
                power = FindValue(_gpu, SensorType.Power, "GPU Package", "GPU Power", "Package");
                coreClock = FindValue(_gpu, SensorType.Clock, "GPU Core", "Core");
                memoryClock = FindValue(_gpu, SensorType.Clock, "GPU Memory", "Memory");
                fan = FindValue(_gpu, SensorType.Control, "GPU Fan", "Fan");
                (vramUsed, vramTotal) = ReadVram(_gpu);
                _readFailures = 0;
            }
            catch (Exception ex)
            {
                if (_readFailures++ == 0)
                {
                    _logger.Warn($"读取 GPU 传感器失败：{ex.Message}");
                }
            }
        }

        float cpuTemperature = BitConverter.Int32BitsToSingle(Volatile.Read(ref _cpuTempBits));

        ushort flags = 0;
        if (!float.IsNaN(gpuTemperature) || !float.IsNaN(power) || !float.IsNaN(coreClock))
        {
            flags |= SensorFlags.GpuValid;
        }

        if (!float.IsNaN(cpuTemperature))
        {
            flags |= SensorFlags.CpuValid;
        }

        if (!float.IsNaN(vramUsed) && !float.IsNaN(vramTotal))
        {
            flags |= SensorFlags.VramValid;
        }

        return new SensorRecord
        {
            QpcTicks = qpc,
            UtcTicks = DateTime.UtcNow.Ticks,
            GpuTemperatureC = gpuTemperature,
            GpuHotSpotC = hotSpot,
            GpuPowerW = power,
            GpuCoreClockMhz = coreClock,
            GpuMemoryClockMhz = memoryClock,
            GpuFanPct = fan,
            VramUsedMb = vramUsed,
            VramTotalMb = vramTotal,
            CpuTemperatureC = cpuTemperature,
            CpuPowerW = float.NaN,
            GpuIndex = 0,
            Flags = flags,
        };
    }

    public void StartCpuMonitor(CancellationToken token)
    {
        _cpuStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _cpuTask = Task.Run(() => CpuMonitorAsync(_cpuStop.Token), CancellationToken.None);
    }

    private async Task CpuMonitorAsync(CancellationToken token)
    {
        int failures = 0;

        while (!token.IsCancellationRequested)
        {
            float? temperature = AcpiThermalZoneReader.TryRead();
            Volatile.Write(ref _cpuTempBits, BitConverter.SingleToInt32Bits(temperature ?? float.NaN));

            TimeSpan delay;
            if (temperature is null)
            {
                failures++;
                if (failures == 1)
                {
                    _logger.Warn("ACPI 热区温度不可用，CPU 温度将显示为 —");
                }

                delay = failures == 1 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(60);
            }
            else
            {
                failures = 0;
                delay = TimeSpan.FromSeconds(5);
            }

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static IHardware? SelectGpu(Computer computer)
    {
        IHardware? best = null;
        int bestScore = int.MinValue;

        foreach (IHardware hardware in computer.Hardware)
        {
            if (hardware.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
            {
                continue;
            }

            int score = hardware.HardwareType switch
            {
                HardwareType.GpuNvidia => 3,
                HardwareType.GpuAmd => 2,
                _ => 1,
            };

            float total = FindVramTotal(hardware);
            if (!float.IsNaN(total) && total > 0)
            {
                score += 2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = hardware;
            }
        }

        return best;
    }

    private static float FindTemperature(IHardware hardware, bool includeHotSpot)
    {
        float? fallback = null;

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value)
            {
                continue;
            }

            string name = sensor.Name;
            bool hot = name.Contains("Hot", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Junction", StringComparison.OrdinalIgnoreCase);

            if (includeHotSpot)
            {
                if (hot)
                {
                    return value;
                }

                continue;
            }

            if (hot)
            {
                continue;
            }

            if (name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Core", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            fallback ??= value;
        }

        return fallback ?? float.NaN;
    }

    private static float FindValue(IHardware hardware, SensorType type, params string[] names)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != type || sensor.Value is not float value)
            {
                continue;
            }

            foreach (string name in names)
            {
                if (sensor.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }

        return float.NaN;
    }

    private static (float Used, float Total) ReadVram(IHardware hardware)
    {
        float used = float.NaN;
        float total = float.NaN;

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.Value is not float value)
            {
                continue;
            }

            bool isUsed = sensor.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase) ||
                sensor.Name.Contains("D3D Dedicated Memory Used", StringComparison.OrdinalIgnoreCase);
            bool isTotal = sensor.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase);

            if (!isUsed && !isTotal)
            {
                continue;
            }

            if (sensor.SensorType == SensorType.SmallData)
            {
                if (isUsed)
                {
                    used = value;
                }
                else
                {
                    total = value;
                }
            }
            else if (sensor.SensorType == SensorType.Data)
            {
                if (isUsed)
                {
                    used = value * 1024;
                }
                else
                {
                    total = value * 1024;
                }
            }
        }

        return (used, total);
    }

    private static float FindVramTotal(IHardware hardware)
    {
        (_, float total) = ReadVram(hardware);
        return total;
    }

    public void Dispose()
    {
        _cpuStop?.Cancel();
        try
        {
            _cpuTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _cpuStop?.Dispose();
        _cpuStop = null;
        _cpuTask = null;

        DisposeComputer();
    }

    private void DisposeComputer()
    {
        if (_computer is null)
        {
            return;
        }

        try
        {
            _computer.Close();
        }
        catch (Exception ex)
        {
            _logger.Warn($"关闭传感器失败：{ex.Message}");
        }

        _computer = null;
        _gpu = null;
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}

/// <summary>
/// Reads the ACPI thermal zone through WMI. This is pure user mode and needs no
/// service or driver, but many desktop boards do not implement the class.
/// </summary>
internal static class AcpiThermalZoneReader
{
    public static float? TryRead()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            float hottest = float.MinValue;
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    object? raw = item["CurrentTemperature"];
                    if (raw is null)
                    {
                        continue;
                    }

                    double tenthsKelvin = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    double celsius = (tenthsKelvin / 10.0) - 273.15;
                    if (celsius > 0 && celsius < 150 && celsius > hottest)
                    {
                        hottest = (float)celsius;
                    }
                }
            }

            return hottest > float.MinValue ? hottest : null;
        }
        catch (ManagementException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
