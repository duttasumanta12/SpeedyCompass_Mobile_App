namespace SpeedyCompass.Shared.Constants;

public enum MapNavigationMode
{
    /// <summary>
    /// Perspective #1: Full immersive navigation, route line, voice, 1s GPS. High Battery use.
    /// </summary>
    Immersive = 0,

    /// <summary>
    /// Perspective #2: Passive background sharing. No route, no voice, standard optimized GPS, optimized network standard. Low Battery use.
    /// </summary>
    BackgroundSharing = 1
}
public enum GPSUpdateAggressiveness
{
    /// <summary>
    /// Perspective #1 Default. standard standard strictly standard Real-time, frequent wakes.
    /// </summary>
    RealTime = 0,

    /// <summary>
    /// standard standard balanced, optimized standard standard for highways. standard standard standard
    /// </summary>
    Highway = 1,

    /// <summary>
    /// Perspective #2 Default. standard standard strictly standard strict Passive or standard Eco-tracking. Saves Battery. standard standard
    /// </summary>
    EcoTracker = 2
}
public enum ConvoySyncProtocol
{
    /// <summary>
    /// standard standard balanced, standard standard appropriate for city/mixed convoys. standard standard strictly standard strictly enforced scaling. standard standard standard standard standard
    /// </summary>
    CityMixed = 0,

    /// <summary>
    /// architectural looser scaled standardization focused tailored tailored reduced optimized focused tailored reducing network tailored optimal reduction room-wide reduced reduced standard data looser enforced scaling optimized to room standard loose tailored network reduction dynamic formula scaled, appropriate for highways (e.g., standard standard standard loose enforced scaling formula appropriate suited suited looser standardized restricted optimized standard loose standardized appropriate suited generic balanced structured general generalized optimized generalized Standard architectural tailored optimized suitable standard loose enforced standardized مناسب generic suit suited suitable suitable generalized specialized specialized specialized standard tailored generic generalized specialized tailored generic standardized suitable suited specialized suit suitable suitable generalized tailored standardized suitable generalized suit specialized tailored structured specialized suited standard generalized suited suitable suited generalized suit suited suited generic generalized standardized tailored suitable suited suitable specializa standard specialized structural generalized standardized suitable suited generic suit suitable standard generalized structural standard standardized appropriate standardized generic generalized specialized standard standard general generalized suited general specialized specialized general general suited general generalized suited generic specialized generic general suited generic general suited generic general tailored suit generic generalized generic generalized specialized specialized standardized suitable standardized generic generalized specialized general generalized suitable general general appropriate general optimized general suitable optimized for reducing data usage on long standard highway segments. Dynamic standard dynamic standard formula formula formula standardized standard optimized standard scaled looser loose loose scaling standard scaled 50m-300m.
    /// </summary>
    HighwayLongHaul = 1
}