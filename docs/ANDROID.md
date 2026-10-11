# Secure Connect для Android

Android-версия форка на **общем коде с десктопом**: интерфейс на Avalonia
(`v2rayN/v2rayN.Android`), а профили, подписки, маршрутизация, генерация
конфигов ядер, пинг и статистика — тот же `ServiceLib`, что и в Windows-версии.
Ветка: `android`.

> Статус: первая сборка, не проверялась на устройстве. Код написан, сборка APK
> идёт в GitHub Actions или скриптом `build-android.ps1`. Дальше — как с
> Windows-версией: логи (экран «Журнал» → «Копировать») и скриншоты → правки.

---

## Как это устроено

```
 приложения ──► TUN (VpnService) ──► hev-socks5-tunnel ──► 127.0.0.1:10808 ──► ядро ──► сервер
                 172.19.0.1/30         (в процессе,          (mixed / socks       sing-box
                 fdfe:dcba:9876::1     P/Invoke)              inbound)             или Xray
```

- **Ядро** (sing-box или Xray — как выбрано для профиля) запускается тем же
  `CoreManager`, что и на ПК, но как обычный локальный прокси: на Android TUN
  даёт система, поэтому `TunModeItem.EnableTun` здесь всегда выключен.
  Официальные Android-сборки ядер лежат в APK как `libsingbox.so` / `libxray.so`
  — это единственный способ получить исполняемый файл в каталоге приложения
  (`nativeLibraryDir`).
- **TUN → SOCKS** делает [hev-socks5-tunnel](https://github.com/heiher/hev-socks5-tunnel)
  (MIT) — та же библиотека, что в v2rayNG. Грузится в процесс через P/Invoke,
  получает fd от `VpnService.Builder.establish()`. UDP идёт через SOCKS5 UDP
  ASSOCIATE.
- **Петли нет**: само приложение (а с ним и процесс ядра) исключено из VPN
  (`addDisallowedApplication(свой пакет)`), поэтому ядро ходит к серверу напрямую.
- **DNS**: системе объявляется `1.1.1.1` внутри туннеля. sing-box перехватывает
  такие запросы своим DNS-модулем (`hijack-dns`), Xray отправляет их через прокси —
  в локальную сеть DNS не утекает.
- **Порядок подключения**: разрешение VPN → сервис → ядро (через общий `Reload`)
  → интерфейс. Если ядро не стартовало, сеть телефона не трогается. Во время
  сессии перезапуск ядра (смена сервера, маршрутов) интерфейс не снимает —
  трафик ждёт, а не уходит мимо туннеля. Упавшее ядро сторожевой таймер
  перезапускает до 3 раз.

### Что из десктопа и чем отличается

| Функция | ПК (Windows) | Android |
|---|---|---|
| Kill switch | Windows Firewall, включается в приложении | Системный: «Постоянная VPN» + «Блокировать подключения без VPN». Приложение показывает состояние и открывает нужный экран настроек |
| Split tunneling: приложения | exe, пути, папки (`BypassApps`) | пакеты Android (`AndroidApps`), выбор из установленных |
| Split tunneling: подсети | правила sing-box | маршруты интерфейса VPN (`BypassSubnets`, общий список) |
| Режимы | «только выбранное» / «всё, кроме выбранного» | те же, общий переключатель `SplitTunnelWhitelist` |
| Доступ к LAN | правило замка | частные сети исключаются из маршрутов VPN |
| Маршруты (NekoBox) | полный редактор | вкл/выкл, порядок, удаление, наборы, пресеты; создание и редактирование правил — пока на ПК |
| Профили и подписки | всё | импорт из буфера (ссылки и адреса подписок), подписки, обновление, пинг, копировать ссылку, удалить |

**Важно про сочетание приложений и подсетей.** На ПК sing-box видит процесс и
может пустить в туннель «приложение ИЛИ подсеть». На Android маршруты действуют
на все приложения внутри VPN, поэтому условия сочетаются через **И**: в туннель
попадает трафик выбранных приложений к выбранным подсетям. И с включённым
«Блокировать подключения без VPN» всё, что вне туннеля, остаётся без сети — это
поведение самого Android.

---

## Сборка

### Вариант 1 — GitHub Actions (проще всего)

1. Запушить ветку `android` (или Actions → **build Android** → Run workflow).
2. Скачать артефакт **SecureConnect-android**: `SecureConnect-android-arm64.apk`
   (телефоны) и `SecureConnect-android-x64.apk` (эмулятор).
3. С полем `release_tag` APK прикрепляются к существующему релизу.

Без секретов подписи APK подписываются одноразовым debug-ключом: каждая сборка
с новой подписью, обновить установленную версию поверх нельзя (только удалить и
поставить заново — настройки при этом теряются). Для обновляемых сборок добавьте
секреты `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASS`, `ANDROID_KEY_ALIAS`,
`ANDROID_KEY_PASS`. Ключ создаётся один раз:

```powershell
keytool -genkeypair -v -keystore secureconnect.keystore -alias secureconnect -keyalg RSA -keysize 4096 -validity 36500
[Convert]::ToBase64String([IO.File]::ReadAllBytes("secureconnect.keystore")) | Set-Clipboard
```

### Вариант 2 — локально на Windows

Один раз: .NET 10 SDK, `dotnet workload install android`, Android Studio
(SDK, **NDK (Side by side)** в SDK Manager → SDK Tools; JDK берётся встроенный),
git.

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\build-android.ps1                       # оба ABI → .\dist\
.\build-android.ps1 -Rids android-arm64   # только телефон
adb install -r dist\SecureConnect-android-arm64.apk
```

Скрипт скачивает sing-box и Xray (официальные Android-сборки), geo-файлы,
собирает hev-socks5-tunnel NDK-ом и публикует APK. Версии: `-SingBoxVersion`,
`-XrayVersion`, `-HevVersion`. В Visual Studio / Rider открывается
`v2rayN/v2rayN.Android.slnx` (после того как скрипт один раз положил нативные
части).

Без нативных частей сборка остановится с понятной ошибкой; только интерфейс —
`-p:AllowMissingNatives=true`.

---

## Первый запуск

1. **Профили** → «Импорт»: из буфера берутся ссылки `vless://`, `vmess://`,
   `ss://`, `trojan://`, `hy2://` … или адрес подписки (`https://…` — добавится
   подписка и сразу обновится). «Подписка» — добавить адрес вручную.
2. Тап по карточке — активный сервер. **Подключение** → кнопка. Android один раз
   спросит разрешение на VPN; на Android 13+ — разрешение на уведомление
   (необязательно).
3. **Kill switch**: Настройки → «Открыть настройки VPN» → шестерёнка у Secure
   Connect → «Постоянная VPN» и «Блокировать подключения без VPN». Чип на главном
   экране станет «Без VPN — блокировка».
4. **Раздельное туннелирование**: Настройки → режим, «Выбрать…» приложения,
   подсети. Изменения применяются к идущей сессии сразу (интерфейс пересоздаётся,
   ядро не перезапускается).

## Если что-то не так

- **Журнал** (Настройки → Журнал → Копировать): вывод ядра, шаги подключения,
  ошибки. Это главное, что нужно прислать.
- С компьютера: `adb logcat -s SecureConnect` — те же строки.
- Конфиг ядра на телефоне: `/data/data/io.github.lkaynonl.secureconnect/files/binConfigs/config.json`
  (доступен через `adb shell run-as io.github.lkaynonl.secureconnect`).

## Файлы

```
v2rayN/v2rayN.Android/
├── AndroidEntry.cs           Application + MainActivity (разрешение VPN, тосты)
├── App.axaml(.cs)            тема Semi (тёмная), стили Secure Connect, палитра
├── Platform/
│   ├── AppHost.cs            старт ServiceLib: каталог данных, ядра из APK, geo-файлы
│   ├── VpnController.cs      подключение/отключение, сторож ядра, состояние kill switch
│   ├── SecureVpnService.cs   VpnService: интерфейс, приложения, маршруты, уведомление
│   ├── TunnelPlan.cs         KillSwitchItem → маршруты/приложения/DNS
│   ├── Cidr.cs               IPv4/IPv6 префиксы и вычитание подсетей
│   ├── HevTunnel.cs          P/Invoke hev-socks5-tunnel
│   ├── InstalledApps.cs      список приложений для выбора
│   ├── LogBuffer.cs          журнал (и logcat)
│   └── Tr.cs                 строки RU/EN
├── Views/                    главный экран, профили, настройки, приложения, маршруты, журнал
├── natives/fetch-natives.sh  нативные части для CI / Linux / macOS
└── AndroidManifest.xml       VpnService (specialUse FGS), разрешения
```

Изменения в общем коде (`ServiceLib`) минимальны и на ПК ничего не меняют:
`PlatformHost` (каталог данных, путь к ядру, «можно ли запускать ядро»),
`KillSwitchItem.AndroidApps`, `CoreManager.IsCoreRunning`,
`MainWindowViewModel.InitTask` и проверка `PlatformHost.CanRunCore` в `Reload`.
