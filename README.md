# 🛡️ gVPN — Portable Windows VPN Client

<p align="center">
  <strong>Быстрый, легковесный и полностью портативный клиент для Windows на базе WPF и Xray-Core.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-blue?style=flat-square&logo=dotnet" alt=".NET 8.0" />
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?style=flat-square&logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/Core-Xray--core-orange?style=flat-square" alt="Xray-core" />
  <img src="https://img.shields.io/badge/Architecture-Single--File%20Portable-success?style=flat-square" alt="Single-File" />
</p>

---

## 🚀 Особенности

- **Единый исполняемый файл (Single-File)**: Приложение компилируется в один `.exe` файл без внешних зависимостей. Среда .NET и ядро туннелирования упакованы внутрь.
- **Поддержка двух ключевых протоколов**:
  - **VLESS-Reality**: Полная маскировка трафика под TLS (обход DPI и блокировок ТСПУ).
  - **WireGuard**: Прямой туннель через встроенный outbound-движок Xray без необходимости устанавливать системные драйверы или службы `wireguard.exe`.
- **Чистая система без мусора**: Конфигурации и рабочие бинарники хранятся в изолированном пользовательском каталоге `%AppData%\gVPN`. Рядом с программой не создаются временные файлы.
- **Кастомный интерфейс на WPF**: Современный тёмный UI с мониторингом скорости трафика в реальном времени (Download / Upload).
- **Оптимизация ресурсов**: Ограничение потребления памяти ядра Go (`GOMEMLIMIT=32MiB`), кэширование сетевых интерфейсов и отсутствие лишних фоновых аллокаций.

---

## 📋 Поддерживаемые форматы добавления

Приложение автоматически распознает входные данные в поле «+ Добавить»:

1. **VLESS ссылки**: Стандартные URI вида:
   ```text
   vless://uuid@server:port?security=reality&sni=...&fp=chrome&pbk=...&sid=...#ServerName

```

2. **Ссылки на подписки**: HTTP/HTTPS URL (поддерживаются как открытые списки, так и Base64-кодированные подписки).
3. **Конфигурации WireGuard**: Обычный текст файла `.conf`:
```ini
[Interface]
PrivateKey = ...
Address = 10.0.0.2/32
DNS = 1.1.1.1

[Peer]
PublicKey = ...
Endpoint = xxx.xxx.xxx.xxx:xxxxx
AllowedIPs = 0.0.0.0/0

```
