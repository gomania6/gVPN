using System;
using System.Collections.Generic;

namespace gVPN
{
    public class VpnProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Новый сервер";
        public string Type { get; set; } = "vless"; // "vless" или "wireguard"
        public string RawData { get; set; } = "";
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