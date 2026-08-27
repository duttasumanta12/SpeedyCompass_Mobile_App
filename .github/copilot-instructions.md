# Copilot Instructions

## Project Guidelines
- Prefers production-standard UI design quality and polish for MAUI screens and components.
- Rerouting must be enabled only for Pro tier users in this codebase. Use plug-and-play handlers for reroute/deviation announcements instead of hardcoded direct voice calls. When changing navigation mode, do not recalculate the route. Keep turn overlay and voice guidance plug-and-play from cached route data/state.
- For PTT in this codebase, audio ducking must apply to incoming voice playback as well, not only when local mic is transmitting.