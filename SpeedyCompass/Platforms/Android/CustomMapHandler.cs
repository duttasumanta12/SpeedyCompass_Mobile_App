using Android.Content;
using Android.Gms.Maps;
using Android.Gms.Maps.Model;
using Android.Graphics;
using Android.Views;
using Microsoft.Maui.Maps.Handlers;
using Microsoft.Maui.Platform;
using SpeedyCompass.Controls;
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
            var mapReady = new MapCallbackHandler(this);
            PlatformView.GetMapAsync(mapReady);
        }

        protected override void DisconnectHandler(MapView platformView)
        {
            CleanupCollectionSubscriptions();
            base.DisconnectHandler(platformView);
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
        private BitmapDescriptor GetOrCreateCanvasIcon(string username, Microsoft.Maui.Graphics.Color userColor)
        {
            if (_usernameIconCache.TryGetValue(username, out var cachedDescriptor)) return cachedDescriptor;

            float density = this.Context.Resources.DisplayMetrics.Density;
            int size = (int)(20 * density); // 20dp simple dot

            var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888);
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
            _usernameIconCache[username] = descriptor;
            return descriptor;
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

                    markerOption.SetIcon(GetOrCreateCanvasIcon(pin.Username, pin.PinColor));
                    markerOption.SetPosition(new LatLng(pin.Location.Latitude, pin.Location.Longitude));
                    markerOption.InvokeZIndex(pin.ZIndex);
                    markerOption.Anchor(0.5f, 0.5f); // Center icon on coordinate
                    markerOption.Flat(true);
                    //markerOption.Rotation((float)pin.Heading);

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

        private BitmapDescriptor GetIcon(string icon, Microsoft.Maui.Graphics.Color color)
        {
            // Cache by name AND color so we only ever generate each color once!
            string cacheKey = $"{icon}_{color.ToArgbHex()}";
            if (_iconMap.TryGetValue(cacheKey, out BitmapDescriptor? value)) return value;

            var drawable = Context.Resources.GetIdentifier(icon, "drawable", Context.PackageName);
            if (drawable == 0) return BitmapDescriptorFactory.DefaultMarker(); // Safe fallback if image is missing

            var bitmap = BitmapFactory.DecodeResource(Context.Resources, drawable);
            var scaled = Bitmap.CreateScaledBitmap(bitmap, 80, 80, false); // Adjust size as needed
            bitmap.Recycle();

            // Apply a blazing-fast native GPU tint to the static white image
            var tintedBitmap = Bitmap.CreateBitmap(scaled.Width, scaled.Height, Bitmap.Config.Argb8888);
            using var canvas = new Canvas(tintedBitmap);
            using var paint = new Paint();
            paint.SetColorFilter(new PorterDuffColorFilter(color.ToPlatform(), PorterDuff.Mode.SrcIn));

            canvas.DrawBitmap(scaled, 0, 0, paint);
            scaled.Recycle();

            var descriptor = BitmapDescriptorFactory.FromBitmap(tintedBitmap);
            _iconMap[cacheKey] = descriptor;
            return descriptor;
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

            int screenHeight = Context.Resources.DisplayMetrics.HeightPixels;
            float density = Context.Resources.DisplayMetrics.Density;

            // Pad the top by 40% of the screen height (pushes the center down)
            int topPadding = (int)(screenHeight * 0.40);

            // Pad the bottom by 180dp (protects the pin from hiding behind the Action Drawer)
            int bottomPadding = (int)(180 * density);

            // Apply the padding to the native Android map engine
            Map.SetPadding(0, topPadding, 0, bottomPadding);

            bool autoTilt = Preferences.Default.Get("Map_AutoTilt", true);
            bool autoZoom = Preferences.Default.Get("Map_AutoZoom", true);
            bool headingUp = Preferences.Default.Get("Map_HeadingUp", false);

            double speedKmh = 0;
            if (!string.IsNullOrEmpty(pin.Speed))
            {
                var speedStr = pin.Speed.Replace(" km/h", "").Replace(" mph", "").Trim();
                double.TryParse(speedStr, out speedKmh);
            }

            // THE FIX: Respect Manual Zoom!
            // Grab exactly where the camera is right now so we don't cause jitter.
            float targetZoom = Map.CameraPosition.Zoom;

            // Only override the zoom if they explicitly enabled Auto-Zoom in settings
            if (autoZoom)
            {
                if (speedKmh > 100) targetZoom = 15f;       // Highway (Zoomed out to see far ahead)
                else if (speedKmh > 60) targetZoom = 16.5f;   // Arterial/City
                else targetZoom = 18f;                      // Slow/Turning (Zoomed in tight)
            }

            // Dynamic Auto-Tilt
            float targetTilt = 0f;
            if (autoTilt && speedKmh > 10)
            {
                targetTilt = 60f; // 3D Horizon view when moving
            }

            // Default to North (0), or respect HeadingUp if toggled
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
        public void ProjectPinsToScreen()
        {
            if (Map == null) return;

            var projection = Map.Projection;
            float density = Context.Resources.DisplayMetrics.Density;

            foreach (var entry in MarkerMap.Values)
            {
                var marker = entry.Marker;
                var pin = entry.Pin;

                // Magic: Google Maps converts LatLng to physical Screen Pixels
                var screenPoint = projection.ToScreenLocation(marker.Position);

                // Pass it back to MAUI (divided by density so it matches XAML coordinates)
                pin.ScreenX = screenPoint.X / density;
                pin.ScreenY = screenPoint.Y / density;
            }
        }
    }

    public class MapCallbackHandler : Java.Lang.Object, IOnMapReadyCallback
    {
        private readonly CustomMapHandler mapHandler;
        public MapCallbackHandler(CustomMapHandler mapHandler) { this.mapHandler = mapHandler; }

        public void OnMapReady(GoogleMap googleMap)
        {
            mapHandler.UpdateValue(nameof(CustomMap.CustomPins));
            googleMap.MarkerClick += mapHandler.MarkerClick;

            googleMap.CameraMove += (s, e) => mapHandler.ProjectPinsToScreen();
            googleMap.PoiClick += (sender, e) =>
            {
                if (mapHandler.VirtualView is CustomMap customMap && e.Poi != null)
                {
                    var loc = new Location(e.Poi.LatLng.Latitude, e.Poi.LatLng.Longitude);

                    // Push it straight up to MAUI XAML!
                    customMap.InvokePoiClicked(loc, e.Poi.Name, e.Poi.PlaceId);
                }
            };
        }
        public void OnPoiClick(PointOfInterest poi)
        {
            if (mapHandler.VirtualView is CustomMap customMap)
            {
                var loc = new Location(poi.LatLng.Latitude, poi.LatLng.Longitude);
                customMap.InvokePoiClicked(loc, poi.Name, poi.PlaceId);
            }
        }
    }
}