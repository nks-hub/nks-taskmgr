using System.Runtime.InteropServices;

namespace NksTaskMgr.Native;

public enum MeasurementUnit : byte { Unknown, Percent, Bytes, BytesPerSecond, BitsPerSecond, Celsius, Seconds, Count, OperationsPerSecond }
public enum MeasurementNormalization : byte { None, WholeMachine, LogicalProcessor, BusiestGpuEngine, DeviceActiveTime }
public enum MeasurementBasis : byte { Unknown, Gauge, ElapsedDelta, CounterRatio, ProviderDefined }
public enum MeasurementIntervalState : byte { Unknown, NotApplicable, Measured, Unavailable }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct MeasurementMetadata
{
    public MeasurementUnit Unit;
    public MeasurementNormalization Normalization;
    public MeasurementBasis Basis;
    public MeasurementIntervalState IntervalState;
    public uint NormalizationDivisor;
    public long ObservedAtNs, IntervalNs, CadenceNs;

    public readonly bool IsValid => (byte)Unit <= (byte)MeasurementUnit.OperationsPerSecond
        && (byte)Normalization <= (byte)MeasurementNormalization.DeviceActiveTime && (byte)Basis <= (byte)MeasurementBasis.ProviderDefined
        && (byte)IntervalState <= (byte)MeasurementIntervalState.Unavailable && ObservedAtNs >= 0 && CadenceNs >= 0
        && (IntervalState == MeasurementIntervalState.Measured ? IntervalNs > 0 && ObservedAtNs > 0 : IntervalNs == 0)
        && (Basis != MeasurementBasis.Gauge || IntervalState == MeasurementIntervalState.NotApplicable);
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ProcessMeasurements
{
    public MeasurementMetadata ProcessCpu, ProcessIo, ProcessMemory, SystemCpu, LogicalCpu, SystemMemory;
    public readonly bool IsValid => ProcessCpu.IsValid && ProcessIo.IsValid && ProcessMemory.IsValid
        && SystemCpu.IsValid && LogicalCpu.IsValid && SystemMemory.IsValid;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ResourceMeasurements
{
    public String16 ResourceId;
    public MeasurementMetadata Primary, Secondary, Tertiary, Temperature, Capacity, SharedCapacity, EngineUtilization;
    public readonly bool IsValid => Primary.IsValid && Secondary.IsValid && Tertiary.IsValid && Temperature.IsValid
        && Capacity.IsValid && SharedCapacity.IsValid && EngineUtilization.IsValid;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct MeasurementData
{
    public uint AbiVersion;
    public FrameFlags Flags;
    public ProcessMeasurements Processes;
    public ResourceMeasurements* Resources;
    public uint ResourceCount, Reserved;
}

public sealed partial class NativeFrame
{
    public unsafe void ReadMeasurements(Action<MeasurementData> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        Use(pointer => {
            NativeMonitor.Check(NativeMethods.GetMeasurementData(pointer, (uint)sizeof(MeasurementData), out var data));
            if (data.AbiVersion != 1 || data.Reserved != 0 || (data.Flags & ~(FrameFlags.ProcessData | FrameFlags.ResourceData)) != 0
                || data.ResourceCount > 4096 || data.ResourceCount != 0 && data.Resources == null
                || (data.Flags & FrameFlags.ProcessData) != 0 && !data.Processes.IsValid)
                throw new NativeMonitorException(NativeResult.AbiMismatch);
            for (uint i = 0; i < data.ResourceCount; ++i) {
                var item = data.Resources[i];
                if (item.ResourceId.Reserved != 0 || item.ResourceId.Length is 0 or > 1024 || item.ResourceId.Data == null || !item.IsValid)
                    throw new NativeMonitorException(NativeResult.AbiMismatch);
            }
            visitor(data);
        });
    }
}

internal static partial class NativeMethods
{
    [LibraryImport(Library, EntryPoint = "nks_frame_get_measurement_data")]
    internal static partial NativeResult GetMeasurementData(nint frame, uint dataSize, out MeasurementData data);
}
