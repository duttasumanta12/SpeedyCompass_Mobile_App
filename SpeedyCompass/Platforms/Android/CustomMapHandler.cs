using Android.Content;
using Android.Gms.Maps;
using Android.Gms.Maps.Model;
using Android.Graphics;
using Android.Views;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Maps.Handlers;
using Microsoft.Maui.Platform;
using SpeedyCompass.Controls;
using SpeedyCompass.Engines;
using SpeedyCompass.Models;
using System.Collections.Specialized;
using System.ComponentModel;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using Path = Android.Graphics.Path;
using Rect = Android.Graphics.Rect;
using RectF = Android.Graphics.RectF;

namespace SpeedyCompass.Platforms.Android
{
    public class CustomMapHandler : MapHandler
    {
        private const int _iconSize = 60;
        private readonly Dictionary<string, BitmapDescriptor> _iconMap = [];
        private INotifyCollectionChanged? _observablePins;
        private readonly Dictionary<string, BitmapDescriptor> _usernameIconCache = [];
        private MapCallbackHandler _mapCallbackHandler; // Track callback handler for cleanup
        private readonly List<Marker> _nativeBubbleMarkers = new();

        // Store the template in memory after reading it once
        private static string _cachedSvgTemplate = null;

        public static new IPropertyMapper<CustomMap, CustomMapHandler> Mapper = new PropertyMapper<CustomMap, CustomMapHandler>(MapHandler.Mapper)
        {
            [nameof(CustomMap.CustomPins)] = MapPinsMapper
        };

        public Dictionary<string, (Marker Marker, RiderPin Pin)> MarkerMap { get; } = [];

        public CustomMapHandler() : base(Mapper)
        {
        }

        protected override void ConnectHandler(MapView platformView)
        {
            base.ConnectHandler(platformView);
            _mapCallbackHandler = new MapCallbackHandler(this);
            PlatformView.GetMapAsync(_mapCallbackHandler);
        }

        protected override void DisconnectHandler(MapView platformView)
        {
            CleanupCollectionSubscriptions();
            
            // Cleanup map callback handler
            _mapCallbackHandler?.Cleanup();
            _mapCallbackHandler?.Dispose();
            _mapCallbackHandler = null;
            
            // Dispose cached bitmaps to free native memory
            foreach (var descriptor in _iconMap.Values)
            {
                // BitmapDescriptor doesn't have direct Dispose, but we can clear the cache
                // to allow the underlying bitmaps to be garbage collected
            }
            _iconMap.Clear();
            
            foreach (var descriptor in _usernameIconCache.Values)
            {
                // Same as above - clear to allow GC
            }
            _usernameIconCache.Clear();
            
            // Unsubscribe from map events
            if (Map != null)
            {
                Map.MarkerClick -= MarkerClick;
            }
            
            // Clear marker map
            MarkerMap.Clear();
            
            base.DisconnectHandler(platformView);
        }

        private void CleanupCollectionSubscriptions()
        {
            if (_observablePins != null)
            {
                _observablePins.CollectionChanged -= OnCustomPinsCollectionChanged;
                _observablePins = null;
            }

            // Unsubscribe from individual pin events to prevent memory leaks
            foreach (var entry in MarkerMap.Values)
            {
                entry.Pin.PropertyChanged -= OnPinPropertyChanged;
            }
        }

        private static void MapPinsMapper(IMapHandler handler, Microsoft.Maui.Maps.IMap map)
        {
            if (handler is CustomMapHandler mapHandler)
            {
                mapHandler.CleanupCollectionSubscriptions();

                if (mapHandler.VirtualView is CustomMap customMap && customMap.CustomPins is INotifyCollectionChanged observable)
                {
                    mapHandler._observablePins = observable;
                    mapHandler._observablePins.CollectionChanged += mapHandler.OnCustomPinsCollectionChanged;
                }

                mapHandler.RefreshPins();
            }
        }

        private void OnCustomPinsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(RefreshPins);
        }

        private void RefreshPins()
        {
            if (Map is null || MauiContext is null) return;

            foreach (var marker in MarkerMap.Values)
            {
                marker.Pin.PropertyChanged -= OnPinPropertyChanged;
                marker.Marker.Remove();
            }

            MarkerMap.Clear();
            AddPins();
        }
        private BitmapDescriptor GetOrCreateCanvasIcon(Microsoft.Maui.Graphics.Color userColor)
        {
            // Cache by the exact Hex color so identical colors share 1 bitmap in memory!
            string cacheKey = $"Dot_{userColor.ToArgbHex()}";

            if (_usernameIconCache.TryGetValue(cacheKey, out var cachedDescriptor))
                return cachedDescriptor;

            float density = this.Context.Resources.DisplayMetrics.Density;
            int size = (int)(20 * density); // 20dp simple dot

            var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888);
            try
            {
                using var canvas = new Canvas(bitmap);
                using var paint = new Paint { AntiAlias = true };

                // White outline
                paint.Color = Color.White;
                paint.SetStyle(Paint.Style.Fill);
                canvas.DrawCircle(size / 2f, size / 2f, size / 2f, paint);

                // Colored inner dot
                paint.Color = userColor.ToPlatform();
                canvas.DrawCircle(size / 2f, size / 2f, (size / 2f) - (2 * density), paint);

                var descriptor = BitmapDescriptorFactory.FromBitmap(bitmap);
                _usernameIconCache[cacheKey] = descriptor; // Store using the Hex Key!
                return descriptor;
            }
            finally
            {
                bitmap?.Recycle();
            }
        }

        private BitmapDescriptor GetHighwayBubbleIcon(List<string> iconNames, string text)
        {
            float density = Context.Resources.DisplayMetrics.Density;

            Typeface materialTypeface = Typeface.CreateFromAsset(Context.Assets, "MaterialSymbols.ttf");
            Typeface standardTypeface = Typeface.Create(Typeface.Default, TypefaceStyle.Bold);

            using var iconPaint = new Paint { AntiAlias = true, TextAlign = Paint.Align.Left, Color = Color.White };
            iconPaint.SetTypeface(materialTypeface);
            iconPaint.TextSize = 22 * density;

            using var textPaint = new Paint { AntiAlias = true, TextAlign = Paint.Align.Left, Color = Color.White };
            textPaint.SetTypeface(standardTypeface);
            textPaint.TextSize = 14 * density;

            // =====================================================================
            // THE FIX: Dynamic Multi-Icon Math Layout
            // =====================================================================
            float padding = 12 * density;
            float spacingBetweenIcons = 4 * density;
            float spacingBeforeWord = 8 * density;
            float textWidth = textPaint.MeasureText(text);

            float totalIconsWidth = 0;
            foreach (var icon in iconNames) totalIconsWidth += iconPaint.MeasureText(icon) + spacingBetweenIcons;
            if (iconNames.Count > 0) totalIconsWidth -= spacingBetweenIcons; // Remove trailing gap

            float width = padding + totalIconsWidth + spacingBeforeWord + textWidth + padding;
            float height = 40 * density;

            var bitmap = Bitmap.CreateBitmap((int)width, (int)height, Bitmap.Config.Argb8888);
            try
            {
                using var canvas = new Canvas(bitmap);

                // 1. Draw the Pill
                using var bgPaint = new Paint { AntiAlias = true, Color = Color.Argb(230, 30, 35, 40) };
                canvas.DrawRoundRect(new RectF(0, 0, width, height), height / 2f, height / 2f, bgPaint);

                float textY = (height / 2f) - ((textPaint.Descent() + textPaint.Ascent()) / 2f);
                float currentX = padding;

                // 2. Loop and Draw ALL Icons (e.g. Bridge + Arrow)
                foreach (var icon in iconNames)
                {
                    canvas.DrawText(icon, currentX, textY + (2 * density), iconPaint);
                    currentX += iconPaint.MeasureText(icon) + spacingBetweenIcons;
                }

                // 3. Draw the Text
                currentX = currentX - spacingBetweenIcons + spacingBeforeWord;
                canvas.DrawText(text, currentX, textY, textPaint);

                return BitmapDescriptorFactory.FromBitmap(bitmap);
            }
            finally
            {
                bitmap?.Recycle();
            }
        }

        // =====================================================================
        // Renders the Bubbles onto the Map
        // =====================================================================
        public void UpdateMapBubbles(IEnumerable<MapBubble> bubbles)
        {
            if (Map == null) return;

            foreach (var marker in _nativeBubbleMarkers) marker.Remove();
            _nativeBubbleMarkers.Clear();

            // Calculate current zoom to set initial visibility safely
            float currentZoom = Map.CameraPosition?.Zoom ?? 15f;
            bool isVisible = currentZoom >= 14.0f; // Only show bubbles at zoom 14+

            foreach (var bubble in bubbles)
            {
                var markerOption = new MarkerOptions();
                markerOption.SetPosition(new LatLng(bubble.Location.Latitude, bubble.Location.Longitude));

                // Pass the LIST of icons!
                markerOption.SetIcon(GetHighwayBubbleIcon(bubble.IconNames, bubble.Instruction));

                markerOption.Flat(false);
                markerOption.Anchor(0.5f, 1.2f);
                markerOption.InvokeZIndex(60f);

                // THE FIX: Apply initial visibility based on zoom!
                markerOption.Visible(isVisible);

                var marker = Map.AddMarker(markerOption);
                _nativeBubbleMarkers.Add(marker);
            }
        }
        public void UpdateBubbleVisibility(float currentZoom)
        {
            bool shouldBeVisible = currentZoom >= 14.0f; // 14 is typical City/Arterial level

            foreach (var marker in _nativeBubbleMarkers)
            {
                if (marker.Visible != shouldBeVisible)
                {
                    marker.Visible = shouldBeVisible;
                }
            }
        }

        private async void AddPins()
        {
            if (VirtualView is CustomMap mapEx && mapEx.CustomPins != null)
            {
                // Ensure the template is loaded into memory before proceeding
                //await EnsureTemplateLoadedAsync();

                foreach (var pin in mapEx.CustomPins)
                {
                    var markerOption = new MarkerOptions();

                    // 1. POPULATE NATIVE INFO WINDOW FIELDS
                    markerOption.SetTitle(pin.Username);
                    markerOption.SetSnippet($"Speed: {pin.Speed}");

                    markerOption.SetIcon(GetOrCreateCanvasIcon(pin.PinColor));
                    markerOption.SetPosition(new LatLng(pin.Location.Latitude, pin.Location.Longitude));
                    markerOption.InvokeZIndex(pin.ZIndex);
                    markerOption.Anchor(0.5f, 0.5f); // Center icon on coordinate
                    markerOption.Flat(true);
                    markerOption.SetRotation((float)pin.Heading);

                    var marker = Map.AddMarker(markerOption);

                    // 2. SUBSCRIBE TO THIS PIN'S PROPERTY CHANGES
                    pin.PropertyChanged += OnPinPropertyChanged;

                    MarkerMap.Add(marker.Id, (marker, pin));
                }
            }
        }
        // 3. THE MAGIC: IN-PLACE MUTATION (No redrawing bitmaps!)
        private void OnPinPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not RiderPin pin) return;

            var entry = MarkerMap.Values.FirstOrDefault(x => x.Pin == pin);
            if (entry.Marker is null) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (e.PropertyName == nameof(RiderPin.Location) || e.PropertyName == nameof(RiderPin.Heading))
                {
                    // Move the tiny Native Map Dot
                    entry.Marker.Position = new LatLng(pin.Location.Latitude, pin.Location.Longitude);
                    entry.Marker.Rotation = (float)pin.Heading;

                    // NEW: Instantly Sync the MAUI UI Overlay!
                    ProjectPinsToScreen();

                    if (pin.Username == "You" && pin.IsAutoCentering && e.PropertyName == nameof(RiderPin.Heading))
                    {
                        UpdateCameraBearing(pin);
                    }
                }
            });
        }
        // =====================================================================
        // THE FIX: BULLETPROOF 3D NAVIGATION PERSPECTIVE
        // =====================================================================
        public void SetNavigationPerspective(bool enableNavPerspective)
        {
            if (Map == null || PlatformView == null) return;

            if (enableNavPerspective)
            {
                // 1. Get the ACTUAL rendered pixel height of the Map, not the phone screen!
                int mapHeight = PlatformView.Height;

                if (mapHeight == 0)
                {
                    // If the layout hasn't finished drawing yet, wait 1 frame and try again
                    PlatformView.Post(() => SetNavigationPerspective(enableNavPerspective));
                    return;
                }

                // 2. Pad the top by 50% of the map's real height.
                // This forces the "center" targeting crosshair down into the bottom 1/4th of the screen!
                int topPad = (int)(mapHeight * 0.7f);

                Map.SetPadding(0, topPad, 0, 0);
            }
            else
            {
                // Reset perfectly to the center
                Map.SetPadding(0, 0, 0, 0);
            }

            // 3. THE FIX: Force the MAUI pins to instantly recalculate their 
            // screen X/Y coordinates so they drop down to match the new optical center!
            ProjectPinsToScreen();
        }

        private BitmapDescriptor GetIcon(string icon, Microsoft.Maui.Graphics.Color color)
        {
            // Cache by name AND color so we only ever generate each color once!
            string cacheKey = $"{icon}_{color.ToArgbHex()}";
            if (_iconMap.TryGetValue(cacheKey, out BitmapDescriptor? value)) 
                return value;

            var drawable = Context.Resources.GetIdentifier(icon, "drawable", Context.PackageName);
            if (drawable == 0) 
                return BitmapDescriptorFactory.DefaultMarker(); // Safe fallback if image is missing

            var bitmap = BitmapFactory.DecodeResource(Context.Resources, drawable);
            if (bitmap == null)
                return BitmapDescriptorFactory.DefaultMarker();

            try
            {
                var scaled = Bitmap.CreateScaledBitmap(bitmap, 80, 80, false);
                try
                {
                    // Apply a blazing-fast native GPU tint to the static white image
                    var tintedBitmap = Bitmap.CreateBitmap(scaled.Width, scaled.Height, Bitmap.Config.Argb8888);
                    try
                    {
                        using var canvas = new Canvas(tintedBitmap);
                        using var paint = new Paint();
                        paint.SetColorFilter(new PorterDuffColorFilter(color.ToPlatform(), PorterDuff.Mode.SrcIn));

                        canvas.DrawBitmap(scaled, 0, 0, paint);

                        var descriptor = BitmapDescriptorFactory.FromBitmap(tintedBitmap);
                        _iconMap[cacheKey] = descriptor;
                        return descriptor;
                    }
                    finally
                    {
                        // CRITICAL: Dispose tintedBitmap after descriptor is created
                        tintedBitmap?.Recycle();
                    }
                }
                finally
                {
                    // CRITICAL: Dispose scaled bitmap
                    scaled?.Recycle();
                }
            }
            finally
            {
                // CRITICAL: Dispose original bitmap
                bitmap?.Recycle();
            }
        }

        public void MarkerClick(object sender, GoogleMap.MarkerClickEventArgs args)
        {
            args.Handled = false; // IMPORTANT: Return false so native Google Maps opens the Info Window!

            if (MarkerMap.TryGetValue(args.Marker.Id, out var value))
            {
                value.Pin.ClickedCommand?.Execute(null);
            }
        }
        private void UpdateCameraBearing(RiderPin pin)
        {
            if (Map == null) return;

            // 1. THE FIX: REMOVE Map.SetPadding!
            // MAUI's Margin="0,0,0,140" already perfectly sizes the map box.
            // Piling Native padding on top of it squished the camera viewport to zero!

            bool autoTilt = Preferences.Default.Get("Map_AutoTilt", true);
            bool autoZoom = Preferences.Default.Get("Map_AutoZoom", true);
            bool headingUp = Preferences.Default.Get("Map_HeadingUp", false);

            double speedKmh = 0;
            if (!string.IsNullOrEmpty(pin.Speed))
            {
                var speedStr = pin.Speed.Replace(" km/h", "").Replace(" mph", "").Trim();
                double.TryParse(speedStr, out speedKmh);
            }

            // Grab exactly where the camera is right now to prevent jitter
            float targetZoom = Map.CameraPosition.Zoom;

            // Only override the zoom if they explicitly enabled Auto-Zoom in settings
            if (autoZoom)
            {
                if (speedKmh > 100) targetZoom = 15f;       // Highway
                else if (speedKmh >= 30) targetZoom = 16.5f;   // Arterial/City
                else targetZoom = 18f;                      // Slow/Turning
            }

            float targetTilt = (autoTilt && speedKmh > 10) ? 60f : 0f;
            float targetBearing = headingUp ? (float)pin.Heading : 0f;

            var cameraPosition = new CameraPosition.Builder()
                .Target(new LatLng(pin.Location.Latitude, pin.Location.Longitude))
                .Bearing(targetBearing)
                .Zoom(targetZoom)
                .Tilt(targetTilt)
                .Build();

            // THE FIX: ALWAYS Animate!
            // Even at high speeds, a 1000ms animation matches our GPS ping rate perfectly,
            // resulting in a flawless 60FPS glide with zero snap-back.
            Map.AnimateCamera(CameraUpdateFactory.NewCameraPosition(cameraPosition), 1000, null);
        }
        private void ResolvePinCollisions()
        {
            // 1. Get active pins and SORT THEM BY NAME. 
            // Sorting is the secret to stopping the flickering. It ensures Pin A 
            // is always drawn at 0 degrees, Pin B at 90 degrees, etc., every single frame!
            var activePins = MarkerMap.Values
                .Select(x => x.Pin)
                .Where(p => p.RawScreenX > -5000 && p.RawScreenY > -5000)
                .OrderBy(p => p.Username)
                .ToList();

            if (activePins.Count == 0) return;

            double clusterThresholdPixels = 65.0; // Distance to consider pins "grouped"
            double baseSpreadRadius = 45.0; // How far to push them out from the center

            // 2. Group the pins into clusters
            List<List<RiderPin>> clusters = new();

            foreach (var pin in activePins)
            {
                bool addedToExisting = false;
                foreach (var cluster in clusters)
                {
                    var centerPin = cluster[0];
                    double dx = pin.RawScreenX - centerPin.RawScreenX;
                    double dy = pin.RawScreenY - centerPin.RawScreenY;
                    double distance = Math.Sqrt(dx * dx + dy * dy);

                    if (distance < clusterThresholdPixels)
                    {
                        cluster.Add(pin);
                        addedToExisting = true;
                        break;
                    }
                }

                if (!addedToExisting)
                {
                    clusters.Add(new List<RiderPin> { pin });
                }
            }

            // 3. Position the pins based on their cluster
            foreach (var cluster in clusters)
            {
                if (cluster.Count == 1)
                {
                    // No collision, just place it normally
                    cluster[0].ScreenX = cluster[0].RawScreenX;
                    cluster[0].ScreenY = cluster[0].RawScreenY;
                }
                else
                {
                    // Calculate the exact center point of the group
                    double centerX = cluster.Average(p => p.RawScreenX);
                    double centerY = cluster.Average(p => p.RawScreenY);

                    // Slightly increase the circle size if there are a LOT of riders clumping
                    double spreadRadius = baseSpreadRadius + (cluster.Count * 3.0);

                    // Arrange them in a perfect circle
                    for (int i = 0; i < cluster.Count; i++)
                    {
                        // Divide 360 degrees (2 * PI) evenly among the riders
                        double angle = (2 * Math.PI / cluster.Count) * i;

                        // Calculate stable X/Y offsets using Sine/Cosine
                        cluster[i].ScreenX = centerX + (spreadRadius * Math.Cos(angle));
                        cluster[i].ScreenY = centerY + (spreadRadius * Math.Sin(angle));
                    }
                }
            }
        }
        public void ProjectPinsToScreen()
        {
            if (Map == null) return;

            var projection = Map.Projection;
            float density = Context.Resources.DisplayMetrics.Density;

            float currentZoom = Map.CameraPosition.Zoom;
            bool isImmersiveMode = currentZoom >= 16.5f;

            foreach (var entry in MarkerMap.Values)
            {
                var marker = entry.Marker;
                var pin = entry.Pin;

                if (pin.Username == "You" && isImmersiveMode)
                {
                    pin.RawScreenX = -10000;
                    pin.RawScreenY = -10000;
                    pin.ScreenX = -10000;
                    pin.ScreenY = -10000;
                    continue;
                }

                // Magic: Google Maps converts LatLng to physical Screen Pixels
                var screenPoint = projection.ToScreenLocation(marker.Position);

                // Pass it back to MAUI (divided by density so it matches XAML coordinates)
                pin.RawScreenX = (int)(screenPoint.X / density);
                pin.RawScreenY = (int)(screenPoint.Y / density);
            }

            ResolvePinCollisions();
        }
        public void UpdateMapTheme(bool isNightMode)
        {
            if (Map == null) return;

            if (isNightMode)
            {
                string darkJson = @"[{""elementType"":""geometry"",""stylers"":[{""color"":""#242f3e""}]},{""elementType"":""labels.text.fill"",""stylers"":[{""color"":""#746855""}]},{""elementType"":""labels.text.stroke"",""stylers"":[{""color"":""#242f3e""}]},{""featureType"":""administrative.locality"",""elementType"":""labels.text.fill"",""stylers"":[{""color"":""#d59563""}]},{""featureType"":""poi"",""elementType"":""labels.text.fill"",""stylers"":[{""color"":""#d59563""}]},{""featureType"":""poi.park"",""elementType"":""geometry"",""stylers"":[{""color"":""#263c3f""}]},{""featureType"":""poi.park"",""elementType"":""labels.text.fill"",""stylers"":[{""color"":""#6b9a76""}]},{""featureType"":""road"",""elementType"":""geometry"",""stylers"":[{""color"":""#38414e""}]},{""featureType"":""road"",""elementType"":""geometry.stroke"",""stylers"":[{""color"":""#212a37""}]},{""featureType"":""road"",""elementType"":""labels.text.fill"",""stylers"":[{""color"":""#9ca5b3""}]},{""featureType"":""road.highway"",""elementType"":""geometry"",""stylers"":[{""color"":""#746855""}]},{""featureType"":""road.highway"",""elementType"":""geometry.stroke"",""stylers"":[{""color"":""#1f2835""}]},{""featureType"":""road.highway"",""elementType"":""labels.text.fill"",""stylers"":[{""color"":""#f3d19c""}]},{""featureType"":""transit"",""elementType"":""geometry"",""stylers"":[{""color"":""#2f3948""}]},{""featureType"":""transit.station"",""elementType"":""labels.text.fill"",""stylers"":[{""color"":""#d59563""}]},{""featureType"":""water"",""elementType"":""geometry"",""stylers"":[{""color"":""#17263c""}]},{""featureType"":""water"",""elementType"":""labels.text.fill"",""stylers"":[{""color"":""#515c6d""}]},{""featureType"":""water"",""elementType"":""labels.text.stroke"",""stylers"":[{""color"":""#17263c""}]}]";
                Map.SetMapStyle(new MapStyleOptions(darkJson));
            }
            else
            {
                Map.SetMapStyle(null); // Restores Daytime colors
            }
        }
    }

    public class MapCallbackHandler : Java.Lang.Object, IOnMapReadyCallback
    {
        private readonly CustomMapHandler _mapHandler;
        private GoogleMap _googleMap;
        
        // Store event handlers to enable proper cleanup
        private EventHandler _cameraMoveHandler;
        private EventHandler<GoogleMap.PoiClickEventArgs> _poiClickHandler;

        public MapCallbackHandler(CustomMapHandler mapHandler) 
        { 
            _mapHandler = mapHandler; 
        }

        public void OnMapReady(GoogleMap googleMap)
        {
            _googleMap = googleMap;
            
            _mapHandler.UpdateValue(nameof(CustomMap.CustomPins));
            googleMap.MarkerClick += _mapHandler.MarkerClick;

            // Fallback to standard theme until the GPS gets a lock!
            bool initialNightMode = Application.Current.RequestedTheme == AppTheme.Dark;
            _mapHandler.UpdateMapTheme(initialNightMode);

            // CRITICAL: Use named methods instead of lambdas for proper cleanup
            _cameraMoveHandler = (s, e) =>
            {
                _mapHandler.ProjectPinsToScreen();

                // THE FIX: Dynamically hide/show bubbles every time the user pinches the screen!
                float currentZoom = googleMap.CameraPosition.Zoom;
                _mapHandler.UpdateBubbleVisibility(currentZoom);
            };
            googleMap.CameraMove += _cameraMoveHandler;
            
            _poiClickHandler = (sender, e) =>
            {
                if (_mapHandler.VirtualView is CustomMap customMap && e.Poi != null)
                {
                    var loc = new Location(e.Poi.LatLng.Latitude, e.Poi.LatLng.Longitude);
                    // Push it straight up to MAUI XAML!
                    customMap.InvokePoiClicked(loc, e.Poi.Name, e.Poi.PlaceId);
                }
            };
            googleMap.PoiClick += _poiClickHandler;
        }

        // CRITICAL: Cleanup method to be called from DisconnectHandler
        public void Cleanup()
        {
            if (_googleMap != null)
            {
                _googleMap.MarkerClick -= _mapHandler.MarkerClick;
                
                if (_cameraMoveHandler != null)
                    _googleMap.CameraMove -= _cameraMoveHandler;
                
                if (_poiClickHandler != null)
                    _googleMap.PoiClick -= _poiClickHandler;
                
                _googleMap = null;
            }
            
            _cameraMoveHandler = null;
            _poiClickHandler = null;
        }

        public void OnPoiClick(PointOfInterest poi)
        {
            if (_mapHandler.VirtualView is CustomMap customMap)
            {
                var loc = new Location(poi.LatLng.Latitude, poi.LatLng.Longitude);
                customMap.InvokePoiClicked(loc, poi.Name, poi.PlaceId);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Cleanup();
            }
            base.Dispose(disposing);
        }
    }
}