using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using SpeedyCompass.Controls;
using SpeedyCompass.Models;
using SpeedyCompass.Services;
using SpeedyCompass.Shared;
using SpeedyCompass.Shared.Constants;

namespace SpeedyCompass;

public partial class LobbyPage
{
    // --- LOCATION PROCESSING & TELEMETRY ---
    private void OnLocalLocationPushedFromBackground(object sender, LocalLocationUpdate e)
    {
        if (_rideCache.RunningInBackground) return; // CPU Shield
        // Instantly queue the hardware update. No blocking!
        _localLocationChannel.Writer.TryWrite(e);
    }
    private void OnRiderLocationUpdated(string riderId, double lat, double lng, double heading, int batteryPct)
    {
        if (_rideCache.RunningInBackground) return; // CPU Shield

        // Instantly queue the network update. No blocking!
        _networkLocationChannel.Writer.TryWrite((riderId, lat, lng, heading, batteryPct));
    }

    // =====================================================================
    // SEQUENTIAL CONSUMER LOOPS
    // =====================================================================
    private async Task ProcessLocalLocationsAsync(CancellationToken token)
    {
        try
        {
            await foreach (var e in _localLocationChannel.Reader.ReadAllAsync(token))
            {
                await ProcessSingleLocalLocationAsync(e);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessNetworkLocationsAsync(CancellationToken token)
    {
        try
        {
            await foreach (var update in _networkLocationChannel.Reader.ReadAllAsync(token))
            {
                // No more Task.Run! Evaluated sequentially.
                var status = _telemetryEngine.CalculateRiderStatus(update.RiderId, new Location(update.Lat, update.Lng), _lastKnownLocation, _rideCache, groupDetails?.Settings);

                if (_rideCache.RunningInBackground) continue;

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    string remoteBatteryStr = update.BatteryPct >= 0 ? $"{update.BatteryPct}%" : "--%";
                    var riderModel = Riders.FirstOrDefault(r => r.Name != null && r.Name.StartsWith(update.RiderId));
                    if (riderModel != null)
                    {
                        riderModel.SpeedStr = status.SpeedStr;
                        riderModel.StatusStr = status.StatusStr;
                        riderModel.StatusColor = status.StatusColor;
                    }

                    if (_riderViewModels.TryGetValue(update.RiderId, out var existingVm))
                    {
                        existingVm.Speed = status.SpeedStr;
                        existingVm.BatteryLevel = remoteBatteryStr;
                        if (_currentNavMode == MapNavigationMode.Immersive)
                        {
                            AnimatePinMovement(existingVm, status.InterpolatedLocation, update.Heading, 1000);
                        }
                        else
                        {
                            // Live Radar (Overview) mode: We keep it as an instant snap to save GPU rendering
                            existingVm.Location = status.InterpolatedLocation;
                            existingVm.Heading = update.Heading;
                        }
                    }
                    else
                    {
                        var colorProfile = GetColorsForRider(update.RiderId);
                        var newVm = new RiderPin(MapPinClicked)
                        {
                            Username = update.RiderId,
                            Speed = status.SpeedStr,
                            Location = status.InterpolatedLocation,
                            Heading = update.Heading,
                            PinColor = colorProfile.PinColor,
                            BatteryLevel = remoteBatteryStr,
                            ZIndex = 50F
                        };
                        _riderViewModels.TryAdd(update.RiderId, newVm);
                        MapPins.Add(newVm);
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessSingleLocalLocationAsync(LocalLocationUpdate e)
    {
        double currentSpeedKmh = e.SpeedMph * 1.60934;

        int batteryPct = Battery.Default.ChargeLevel >= 0 ? (int)(Battery.Default.ChargeLevel * 100) : -1;
        string batteryStr = batteryPct >= 0 ? $"{batteryPct}%" : "--%";

        if (!_rideCache.RunningInBackground)
        {
            EvaluateDayNightCycle(e.Location);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                LocationDisabledOverlay.Hide();
                if (_myPinVm != null)
                {
                    string newSpeedStr = $"{Math.Round(currentSpeedKmh)} km/h";
                    if (currentSpeedKmh > _rideCache.MaxSpeedKmh)
                    {
                        _rideCache.MaxSpeedKmh = currentSpeedKmh;
                        TelemetryHeaderControl.UpdateTopSpeed($"{Math.Round(_rideCache.MaxSpeedKmh)} km/h");
                    }

                    TelemetryHeaderControl.UpdateSpeed(newSpeedStr);
                    Location displayLocation = e.Location; // Default to raw GPS

                    if (groupDetails?.CurrentState == GroupState.Navigating &&
                        _rideCache.CurrentRoutePoints != null &&
                        _rideCache.CurrentRoutePoints.Any())
                    {
                        // Pull the dot onto the blue line!
                        displayLocation = _routingEngine.SnapToRouteLine(e.Location, _rideCache.CurrentRoutePoints, _rideCache.CurrentRouteIndex);
                    }
                    AnimatePinMovement(_myPinVm, e.Location, e.Heading, 1000);
                    _myPinVm.Speed = newSpeedStr;
                    _myPinVm.BatteryLevel = batteryStr;

                    var myModel = Riders.FirstOrDefault(r => r.GoogleId == CurrentGoogleId);
                    if (myModel != null)
                    {
                        myModel.SpeedStr = newSpeedStr;
                        myModel.StatusStr = "Local";
                        myModel.StatusColor = Colors.Transparent;
                    }
                }

                if (MapFollowButton.IsVisible && (DateTime.Now - _lastAutoFrameTime).TotalSeconds > 15)
                {
                    _lastAutoFrameTime = DateTime.Now;
                    FitMapToBounds();
                }
            });
        }

        _lastKnownLocation = e.Location;

        // =====================================================================
        // CATCH-UP PROTOCOL: AUTO-SNAPPER
        // Check if we finally arrived at the Admin's parking spot!
        // =====================================================================
        if (_rideCache.PendingCatchUpState != null)
        {
            var adminLoc = GetAdminLocation();
            if (adminLoc != null)
            {
                double distToAdminKm = Location.CalculateDistance(_lastKnownLocation, adminLoc, DistanceUnits.Kilometers);

                // If we close the gap to under 200 meters, we have arrived!
                if (distToAdminKm <= 0.2)
                {
                    AppLogger.Info("CatchUp", "Rider caught up to Admin. Applying pending state.");
                    _voiceEngine.Speak("You have caught up with the group.");

                    var targetState = _rideCache.PendingCatchUpState.Value;
                    _rideCache.PendingCatchUpState = null; // Clear it to allow the transition

                    // Force the sync so the UI finally snaps to Paused/Completed
                    ChangeGroupState(targetState, "Auto-CatchUp", forceSync: true).SafeFireAndForget();
                }
            }
        }

        if (ShouldBroadcastToNetwork(e.Location, currentSpeedKmh))
        {
            _rideCache.LastNetworkBroadcastTime = DateTime.UtcNow;
            _rideCache.LastBroadcastLocation = e.Location;

            List<string> excludedPeers;
            lock (_rideCache.UsersWhoMutedMe) { excludedPeers = _rideCache.UsersWhoMutedMe.ToList(); }

            // Explicitly tell the server NOT to route this payload to these users
            await _signalRService.UpdateLocation(groupDetails.GroupName, _myName, e.Location.Latitude, e.Location.Longitude, e.Heading, batteryPct, excludedPeers);
        }

        if (groupDetails?.CurrentState == GroupState.Navigating)
        {
            // FEATURE 1: ELEVATION ANNOUNCER & UI
            if (e.Location.Altitude.HasValue)
            {
                double currentAlt = e.Location.Altitude.Value;

                // Push raw value directly to the UI Header
                MainThread.BeginInvokeOnMainThread(() => TelemetryHeaderControl.UpdateElevation($"{Math.Round(currentAlt)} m"));

                if (_rideCache.LastAnnouncedElevation == null)
                {
                    _rideCache.LastAnnouncedElevation = currentAlt;
                }
                else if (Math.Abs(currentAlt - _rideCache.LastAnnouncedElevation.Value) >= 100)
                {
                    _announceElevation?.Invoke(currentAlt);
                    _rideCache.LastAnnouncedElevation = currentAlt;
                }
            }

            // FEATURE 3: NON-IRRITATING AUTO-STOP DETECTION
            if (currentSpeedKmh < 3) // Below 3 km/h is considered stopped
            {
                if (_rideCache.StopStartTime == null) _rideCache.StopStartTime = DateTime.Now;
                else if ((DateTime.Now - _rideCache.StopStartTime.Value).TotalSeconds > 45)
                {
                    if ((DateTime.Now - _rideCache.LastAutoPausePromptTime).TotalMinutes > 10)
                    {
                        _rideCache.LastAutoPausePromptTime = DateTime.Now;
                        TriggerAutoPausePrompt().SafeFireAndForget();
                    }
                }
            }
            else
            {
                _rideCache.StopStartTime = null; // Reset stop timer the moment they move
            }

            var lastCrumb = _rideCache.DrivenBreadcrumbs.LastOrDefault();
            if (lastCrumb == null || Location.CalculateDistance(lastCrumb, e.Location, DistanceUnits.Kilometers) > 0.05)
            {
                lock (_rideCache.DrivenBreadcrumbs)
                {
                    _rideCache.DrivenBreadcrumbs.Add(e.Location);
                }
            }

            e.Location.Speed = currentSpeedKmh / 3.6;
            e.Location.Course = e.Heading;

            if (_currentNavMode == MapNavigationMode.Immersive)
            {
                if (_tierService.UseLiveTraffic)
                {
                    try
                    {
                        await _routingEngine.RefreshTrafficWindowIfNeededAsync(
                            e.Location,
                            currentSpeedKmh,
                            _rideCts?.Token ?? CancellationToken.None);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Traffic", ex, "Traffic window refresh failed.");
                    }
                }

                if (_tierService.UseImmersiveTbt)
                {
                    await TrimRouteVisuals(e.Location);
                }
                else
                {
                    // FREE TIER LOGIC
                    if (_rideCache.LastOdometerLocation != null)
                    {
                        double stepDist = Location.CalculateDistance(_rideCache.LastOdometerLocation, e.Location, DistanceUnits.Kilometers);
                        if (stepDist > 0.01 && stepDist < 20) _rideCache.CumulativeDistanceKm += stepDist;
                    }
                    _rideCache.LastOdometerLocation = e.Location;

                    UpdateFreeTierTelemetry(e.Location, currentSpeedKmh);
                    await CheckMeetupProximityUnifiedAsync(e.Location);
                }
            }
            else
            {
                if (_rideCache.LastOdometerLocation != null)
                {
                    double stepDist = Location.CalculateDistance(_rideCache.LastOdometerLocation, e.Location, DistanceUnits.Kilometers);
                    if (stepDist > 0.01 && stepDist < 20) _rideCache.CumulativeDistanceKm += stepDist;
                }
                _rideCache.LastOdometerLocation = e.Location;

                // FIX: Keep telemetry/progress UI alive in BackgroundSharing mode
                UpdateFreeTierTelemetry(e.Location, currentSpeedKmh);
                await CheckMeetupProximityUnifiedAsync(e.Location);
            }
        }
    }

    private async Task TriggerAutoPausePrompt()
    {
        MainThread.BeginInvokeOnMainThread(() => AutoPauseBanner.IsVisible = true);
        _voiceEngine.Speak("It looks like you've stopped. Tap your screen if you need to pause the ride.");

        try
        {
            await Task.Delay(5000, _lifecycleCts.Token);
        }
        catch { }

        MainThread.BeginInvokeOnMainThread(() => AutoPauseBanner.IsVisible = false);
    }

    private void OnAutoPauseTapped(object sender, TappedEventArgs e)
    {
        AutoPauseBanner.IsVisible = false;
        OnPauseNavClicked(this, EventArgs.Empty);
    }
}