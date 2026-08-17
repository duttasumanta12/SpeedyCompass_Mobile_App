using SpeedyCompass.Shared.Models;

namespace SpeedyCompass.Services;

public readonly record struct BroadcastPolicyInput(
    Location? LastBroadcastLocation,
    DateTime LastNetworkBroadcastTimeUtc,
    Location CurrentLocation,
    double SpeedKmh,
    GroupDetailsDto? GroupDetails,
    int AggressivenessMode,
    int BatteryThrottlePercentage,
    double BatteryLevelPercent,
    BatteryState BatteryState,
    double LocalMinUpdateMeters,
    double LocalMaxUpdateMeters);

public interface ILocationBroadcastPolicy
{
    bool ShouldBroadcast(in BroadcastPolicyInput input);
}
public sealed class LocationBroadcastPolicy : ILocationBroadcastPolicy
{
    public bool ShouldBroadcast(in BroadcastPolicyInput input)
    {
        if (input.LastBroadcastLocation == null) return true;

        double timeSinceLastSeconds = (DateTime.UtcNow - input.LastNetworkBroadcastTimeUtc).TotalSeconds;

        bool isLowBattery =
            input.BatteryLevelPercent > 0 &&
            input.BatteryLevelPercent <= input.BatteryThrottlePercentage &&
            input.BatteryState != BatteryState.Charging;

        if (input.AggressivenessMode == 1 || isLowBattery)
            return timeSinceLastSeconds >= 15;

        if (timeSinceLastSeconds >= 10)
            return true;

        double minUpdateDist = 10;
        double maxUpdateDist = 100;

        if (input.AggressivenessMode == 2)
        {
            minUpdateDist = input.LocalMinUpdateMeters;
            maxUpdateDist = input.LocalMaxUpdateMeters;
        }
        else
        {
            int protocol = (int)(input.GroupDetails?.Settings?.ConvoyUpdateProtocol ?? 0);
            minUpdateDist = protocol == 0 ? (input.GroupDetails?.Settings?.MinUpdateDistanceMeters ?? 10) : 50;
            maxUpdateDist = protocol == 0 ? (input.GroupDetails?.Settings?.MaxUpdateDistanceMeters ?? 100) : 300;
        }

        double distSinceLastMeters =
            Location.CalculateDistance(input.LastBroadcastLocation, input.CurrentLocation, DistanceUnits.Kilometers) * 1000;

        double speedRatio = Math.Min(input.SpeedKmh, 100.0) / 100.0;
        double dynamicThresholdMeters = minUpdateDist + (speedRatio * (maxUpdateDist - minUpdateDist));

        return distSinceLastMeters >= dynamicThresholdMeters;
    }
}