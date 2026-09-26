using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SpeedyCompass.Models
{
    public class AppTierInfo : INotifyPropertyChanged
    {
        private bool _isAvailable;
        private bool _isSelected;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string PriceText { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ThemeColor { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string BadgeText { get; set; } = string.Empty;
        public List<string> Features { get; set; } = new();

        public bool IsAvailable
        {
            get => _isAvailable;
            set => SetProperty(ref _isAvailable, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetProperty<T>(ref T backingField, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(backingField, value)) return false;
            backingField = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}