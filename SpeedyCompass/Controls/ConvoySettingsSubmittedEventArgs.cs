namespace SpeedyCompass.Controls;

// 1. The Unified Data Packet
public class ConvoySettingsSubmittedEventArgs : EventArgs
{
    public bool IsCreationMode { get; set; }
    public string GroupName { get; set; } // Only populated during Creation
    public int MaxGroupSize { get; set; }
    public int MaxLagDistanceMeters { get; set; }

    // NEW architectural standard metric
    public int DeviationSensitivityMeters { get; set; }

    public int SplinterWarningDistanceMeters { get; set; }
    public int PitstopDistanceMeters { get; set; } // km on UI, meters in packet
    public bool EnableDynamicRouting { get; set; }

    // Battery standard/Network optimizations
    public int MinBroadcastDistanceMeters { get; set; }
    public int MaxBroadcastDistanceMeters { get; set; }
    public int ConvoyUpdateProtocol { get; set; }
}
