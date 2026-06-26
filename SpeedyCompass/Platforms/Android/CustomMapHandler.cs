using Android.Content;
using Android.Gms.Maps;
using Android.Gms.Maps.Model;
using Android.Graphics;
using SpeedyCompass.Controls;
using SpeedyCompass.Models;
using Microsoft.Maui.Maps.Handlers;
using System.Collections.Specialized; // Added for INotifyCollectionChanged

namespace SpeedyCompass.Platforms.Android
{
    public class CustomMapHandler : MapHandler
    {
        private const int _iconSize = 60;
        private readonly Dictionary<string, BitmapDescriptor> _iconMap = [];

        // Track the collection to subscribe/unsubscribe from events
        private INotifyCollectionChanged _observablePins;

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
            // Prevent memory leaks by unsubscribing when the map is destroyed
            if (_observablePins != null)
            {
                _observablePins.CollectionChanged -= OnCustomPinsCollectionChanged;
                _observablePins = null;
            }
            base.DisconnectHandler(platformView);
        }

        private static void MapPinsMapper(IMapHandler handler, Microsoft.Maui.Maps.IMap map)
        {
            if (handler is CustomMapHandler mapHandler)
            {
                // 1. Unsubscribe from old collection if it exists
                if (mapHandler._observablePins != null)
                {
                    mapHandler._observablePins.CollectionChanged -= mapHandler.OnCustomPinsCollectionChanged;
                }

                // 2. Subscribe to the new collection's changes
                if (mapHandler.VirtualView is CustomMap customMap && customMap.CustomPins is INotifyCollectionChanged observable)
                {
                    mapHandler._observablePins = observable;
                    mapHandler._observablePins.CollectionChanged += mapHandler.OnCustomPinsCollectionChanged;
                }

                // 3. Draw initial pins
                mapHandler.RefreshPins();
            }
        }

        private void OnCustomPinsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // Ensure UI updates run on the Main Thread when the ObservableCollection changes
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RefreshPins();
            });
        }

        private void RefreshPins()
        {
            if (Map is null || MauiContext is null)
            {
                return;
            }

            foreach (var marker in MarkerMap)
            {
                marker.Value.Marker.Remove();
            }

            MarkerMap.Clear();
            AddPins();
        }

        private BitmapDescriptor GetIcon(string icon)
        {
            if (_iconMap.TryGetValue(icon, out BitmapDescriptor value))
            {
                return value;
            }

            var drawable = Context.Resources.GetIdentifier(icon, "drawable", Context.PackageName);
            var bitmap = BitmapFactory.DecodeResource(Context.Resources, drawable);
            var scaled = Bitmap.CreateScaledBitmap(bitmap, _iconSize, _iconSize, false);
            bitmap.Recycle();
            var descriptor = BitmapDescriptorFactory.FromBitmap(scaled);

            _iconMap[icon] = descriptor;

            return descriptor;
        }

        private void AddPins()
        {
            if (VirtualView is CustomMap mapEx && mapEx.CustomPins != null)
            {
                foreach (var pin in mapEx.CustomPins)
                {
                    var markerOption = new MarkerOptions();
                    markerOption.SetTitle(string.Empty);
                    markerOption.SetIcon(GetIcon(pin.ImageSource));
                    markerOption.SetPosition(new LatLng(pin.Location.Latitude, pin.Location.Longitude));
                    var marker = Map.AddMarker(markerOption);

                    MarkerMap.Add(marker.Id, (marker, pin));
                }
            }
        }

        public void MarkerClick(object sender, GoogleMap.MarkerClickEventArgs args)
        {
            if (MarkerMap.TryGetValue(args.Marker.Id, out (Marker Marker, RiderPin Pin) value))
            {
                value.Pin.ClickedCommand?.Execute(null);
            }
        }
    }

    public class MapCallbackHandler : Java.Lang.Object, IOnMapReadyCallback
    {
        private readonly CustomMapHandler mapHandler;

        public MapCallbackHandler(CustomMapHandler mapHandler)
        {
            this.mapHandler = mapHandler;
        }

        public void OnMapReady(GoogleMap googleMap)
        {
            mapHandler.UpdateValue(nameof(CustomMap.CustomPins));
            googleMap.MarkerClick += mapHandler.MarkerClick;
        }
    }
}