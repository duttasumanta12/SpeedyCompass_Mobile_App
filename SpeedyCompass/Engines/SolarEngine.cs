namespace SpeedyCompass.Engines;

public static class SolarEngine
{
    public static bool IsNight(double latitude, double longitude)
    {
        var now = DateTime.UtcNow;
        int dayOfYear = now.DayOfYear;

        // 1. Calculate the approximate solar declination (Earth's tilt)
        double declination = 23.45 * Math.Sin((284.0 + dayOfYear) * 360.0 / 365.0 * Math.PI / 180.0);

        // 2. Calculate the hour angle for sunrise/sunset (Zenith ~90.83 degrees for atmospheric refraction)
        double zenith = 90.83 * Math.PI / 180.0;
        double latRad = latitude * Math.PI / 180.0;
        double decRad = declination * Math.PI / 180.0;

        double cosHourAngle = (Math.Cos(zenith) - (Math.Sin(latRad) * Math.Sin(decRad))) / (Math.Cos(latRad) * Math.Cos(decRad));

        // 3. Handle Extreme Latitudes (Polar night or Midnight sun)
        if (cosHourAngle < -1) return false;
        if (cosHourAngle > 1) return true;

        double hourAngle = Math.Acos(cosHourAngle) * 180.0 / Math.PI;

        // 4. Calculate exact sunrise and sunset in UTC hours
        double sunTransit = 12.0 - (longitude / 15.0);
        double sunrise = sunTransit - (hourAngle / 15.0);
        double sunset = sunTransit + (hourAngle / 15.0);

        // 5. Compare with the current UTC time
        double currentUtcDecimal = now.TimeOfDay.TotalHours;

        return currentUtcDecimal < sunrise || currentUtcDecimal > sunset;
    }
}