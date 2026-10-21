using System.Runtime.Versioning;

namespace gVPN
{
    [SupportedOSPlatform("windows")]
    public static class LocalizationService
    {
        public static void UpdateUi(MainWindow w, string lang, bool isConnected, string? profileName)
        {
            switch (lang)
            {
                case "en":
                    w.LblAvailableServers.Text = "Available Servers";
                    w.BtnToggleAddPanel.Content = w.PnlAddServer.Visibility == System.Windows.Visibility.Visible ? "Hide Form" : "+ Add";
                    w.LblAddHint.Text = "Paste vless://, subscription URL, or WG config:";
                    w.BtnAdd.Content = "Save Connection";
                    w.BtnConnect.Content = isConnected ? "Disconnect" : "Connect";
                    w.TxtStatus.Text = isConnected ? $"Connected: {profileName}" : "Status: Disconnected";
                    w.LblGeneralSettings.Text = "General Settings";
                    w.LblLanguage.Text = "Interface Language";
                    w.ChkAutoStartApp.Content = "Start gVPN with Windows";
                    w.ChkAutoStartConnect.Content = "Automatically connect selected server";
                    w.ChkStartMinimized.Content = "Start minimized to system tray";
                    w.LblRoutingTitle.Text = "Traffic Routing Mode";
                    w.RbTunnelAll.Content = "All PC traffic (Global VPN)";
                    w.RbTunnelApps.Content = "Only selected applications";
                    w.LblSelectedApps.Text = "Selected applications (.exe):";
                    w.BtnAddApp.Content = "+ Add .exe";
                    w.BtnBackToServers.Content = "← Back to Servers";
                    break;

                case "pl":
                    w.LblAvailableServers.Text = "Dostępne serwery";
                    w.BtnToggleAddPanel.Content = w.PnlAddServer.Visibility == System.Windows.Visibility.Visible ? "Ukryj" : "+ Dodaj";
                    w.LblAddHint.Text = "Wklej link vless://, subskrypcję lub plik WG:";
                    w.BtnAdd.Content = "Zapisz połączenie";
                    w.BtnConnect.Content = isConnected ? "Rozłącz" : "Połącz";
                    w.TxtStatus.Text = isConnected ? $"Połączono: {profileName}" : "Status: Rozłączono";
                    w.LblGeneralSettings.Text = "Ustawienia ogólne";
                    w.LblLanguage.Text = "Język interfejsu";
                    w.ChkAutoStartApp.Content = "Uruchom gVPN wraz z systemem Windows";
                    w.ChkAutoStartConnect.Content = "Automatycznie połącz wybrany serwer";
                    w.ChkStartMinimized.Content = "Uruchom zminimalizowany w zasobniku";
                    w.LblRoutingTitle.Text = "Tryb routingu ruchu";
                    w.RbTunnelAll.Content = "Cały ruch komputera (Globalny VPN)";
                    w.RbTunnelApps.Content = "Tylko wybrane aplikacje";
                    w.LblSelectedApps.Text = "Wybrane aplikacje (.exe):";
                    w.BtnAddApp.Content = "+ Dodaj .exe";
                    w.BtnBackToServers.Content = "← Wróć do serwerów";
                    break;

                default: // ru
                    w.LblAvailableServers.Text = "Доступные серверы";
                    w.BtnToggleAddPanel.Content = w.PnlAddServer.Visibility == System.Windows.Visibility.Visible ? "Скрыть форму" : "+ Добавить";
                    w.LblAddHint.Text = "Вставьте vless://, URL подписки или WG конфиг:";
                    w.BtnAdd.Content = "Сохранить подключение";
                    w.BtnConnect.Content = isConnected ? "Отключиться" : "Подключиться";
                    w.TxtStatus.Text = isConnected ? $"Подключено: {profileName}" : "Статус: Отключено";
                    w.LblGeneralSettings.Text = "Общие параметры";
                    w.LblLanguage.Text = "Язык интерфейса";
                    w.ChkAutoStartApp.Content = "Запускать gVPN вместе с Windows";
                    w.ChkAutoStartConnect.Content = "Автоматически подключать выбранный сервер";
                    w.ChkStartMinimized.Content = "Запускать свернутым в системный трей";
                    w.LblRoutingTitle.Text = "Режим маршрутизации трафика";
                    w.RbTunnelAll.Content = "Весь трафик ПК (Все приложения)";
                    w.RbTunnelApps.Content = "Только выбранные приложения";
                    w.LblSelectedApps.Text = "Список приложений (.exe):";
                    w.BtnAddApp.Content = "+ Выбрать .exe";
                    w.BtnBackToServers.Content = "← Назад к серверам";
                    break;
            }
        }
    }
}