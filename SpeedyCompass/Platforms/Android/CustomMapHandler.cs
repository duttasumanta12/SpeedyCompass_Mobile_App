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
            if (_usernameIconCache.TryGetValue(username, out var cachedDescriptor))
            {
                return cachedDescriptor;
            }

            // 1. Get Screen Density to ensure it looks sharp on all screen sizes
            float density = this.Context.Resources.DisplayMetrics.Density;

            // 2. Define our sizes (scaled by screen density)
            float textSize = 14f * density;
            float paddingX = 12f * density;
            float paddingY = 8f * density;
            float cornerRadius = 8f * density;
            float tailWidth = 12f * density;
            float tailHeight = 8f * density;

            // 3. Setup the Paints (The "Brushes" we use to draw)
            using var backgroundPaint = new Paint { AntiAlias = true };
            backgroundPaint.Color = Color.ParseColor("#55575A"); // Dark grey bubble from your screenshot
            backgroundPaint.SetStyle(Paint.Style.Fill);

            using var textPaint = new Paint { AntiAlias = true };
            textPaint.Color = Color.White;
            textPaint.TextSize = textSize;
            textPaint.FakeBoldText = true;
            textPaint.TextAlign = Paint.Align.Center; // Centers text automatically on the X axis

            // 4. Measure the text to figure out how big our bubble needs to be
            var textBounds = new Rect();
            textPaint.GetTextBounds(username, 0, username.Length, textBounds);

            float bubbleWidth = textBounds.Width() + (paddingX * 2);
            float bubbleHeight = textBounds.Height() + (paddingY * 2);

            // The total height includes the bubble + the little tail pointing down
            float totalHeight = bubbleHeight + tailHeight;

            // 5. Create the empty Bitmap and Canvas
            var bitmap = Bitmap.CreateBitmap((int)bubbleWidth, (int)totalHeight, Bitmap.Config.Argb8888);
            using var canvas = new Canvas(bitmap);

            // 6. Draw the rounded rectangle (The main bubble)
            var rect = new RectF(0, 0, bubbleWidth, bubbleHeight);
            canvas.DrawRoundRect(rect, cornerRadius, cornerRadius, backgroundPaint);

            // 7. Draw the Triangle Tail pointing down
            using var tailPath = new Path();
            float centerX = bubbleWidth / 2f;

            tailPath.MoveTo(centerX - (tailWidth / 2f), bubbleHeight); // Top left of tail
            tailPath.LineTo(centerX + (tailWidth / 2f), bubbleHeight); // Top right of tail
            tailPath.LineTo(centerX, totalHeight);                     // Bottom point of tail
            tailPath.Close();

            // Create a Paint for the tail using the userColor
            using var tailPaint = new Paint { AntiAlias = true };

            tailPaint.Color = userColor.ToPlatform(); // Convert MAUI Color to Android Color
            tailPaint.SetStyle(Paint.Style.Fill);

            // Draw the tail with the user color
            canvas.DrawPath(tailPath, tailPaint);

            // 8. Draw the Text (Centered vertically and horizontally)
            // Font math: We calculate the vertical center based on the font's Ascent and Descent
            float textY = (bubbleHeight / 2f) - ((textPaint.Descent() + textPaint.Ascent()) / 2f);
            canvas.DrawText(username, centerX, textY, textPaint);

            // 9. Convert to Google Maps format and cache it!
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
                    markerOption.Anchor(0.5f, 1.0f); // Center icon on coordinate

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
                if (e.PropertyName == nameof(RiderPin.Location))
                {
                    // Smoothly teleport marker to new GPS coordinate
                    entry.Marker.Position = new LatLng(pin.Location.Latitude, pin.Location.Longitude);
                }
                else if (e.PropertyName == nameof(RiderPin.Speed))
                {
                    // Update snippet text
                    entry.Marker.Snippet = $"Speed: {pin.Speed}";

                    // If the user currently has this pin's bubble open on their screen, force it to refresh!
                    if (entry.Marker.IsInfoWindowShown)
                    {
                        entry.Marker.ShowInfoWindow();
                    }
                }
                else if(e.PropertyName == nameof(RiderPin.Heading))
                {
                    // 1. Move the marker
                    entry.Marker.Position = new LatLng(pin.Location.Latitude, pin.Location.Longitude);

                    // 2. ONLY rotate/move the camera if this is the CURRENT user's pin
                    // (You don't want the map spinning wildly when other riders turn corners!)
                    if (pin.Username == "You" && pin.IsAutoCentering)
                    {
                        UpdateCameraBearing(pin);
                    }
                }
            });
        }

        private BitmapDescriptor GetIcon(string icon)
        {
            if (_iconMap.TryGetValue(icon, out BitmapDescriptor? value)) return value;

            var drawable = Context.Resources.GetIdentifier(icon, "drawable", Context.PackageName);
            var bitmap = BitmapFactory.DecodeResource(Context.Resources, drawable);
            var scaled = Bitmap.CreateScaledBitmap(bitmap, _iconSize, _iconSize, false);
            bitmap.Recycle();
            var descriptor = BitmapDescriptorFactory.FromBitmap(scaled);

            _iconMap[icon] = descriptor;
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

            // Build a new camera position
            var cameraPosition = new CameraPosition.Builder()
                .Target(new LatLng(pin.Location.Latitude, pin.Location.Longitude)) // Keep user centered
                .Bearing((float)pin.Heading)                                       // Rotate the map!
                .Zoom(Map.CameraPosition.Zoom)                                     // Maintain current zoom level
                .Tilt(45f)                                                         // Optional: Give it that angled 3D GPS look
                .Build();

            // Use AnimateCamera for a smooth transition (MoveCamera is instant/choppy)
            Map.AnimateCamera(CameraUpdateFactory.NewCameraPosition(cameraPosition));
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
        }
    }
}