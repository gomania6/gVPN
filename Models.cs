using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace gVPN
{
    public class VpnProfile : INotifyPropertyChanged
    {
        private string _name = "Новый сервер";
        private string _latencyText = "";
        private string _latencyColor = "#A6ADC8";

        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        public string Type { get; set; } = "vless"; // "vless", "trojan", "shadowsocks", "wireguard"
        public string RawData { get; set; } = "";

        [JsonIgnore]
        public string LatencyText
        {
            get => _latencyText;
            set { if (_latencyText != value) { _latencyText = value; OnPropertyChanged(); } }
        }

        [JsonIgnore]
        public string LatencyColor
        {
            get => _latencyColor;
            set { if (_latencyColor != value) { _latencyColor = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class AppSettings
    {
        public string Language { get; set; } = "ru"; // ru, en, pl
        public bool AutoStartApp { get; set; } = false;
        public bool AutoStartConnect { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public string? LastSelectedProfileId { get; set; }
        public bool TunnelOnlySelectedApps { get; set; } = false;
        public List<string> TargetAppPaths { get; set; } = new();
    }
}