# v2rayN → «Secure Connect» fork — дорожная карта

Форк **v2rayN v7 (Avalonia)** с функционалом VPN-туннелирования в духе
**WireSock Secure Connect**: системный kill switch, split tunneling по
приложениям/подсетям и переработанный интерфейс.

База: `2dust/v2rayN` (ветка `main`, .NET 10). Общая логика — в `ServiceLib`,
фронтенды — `v2rayN` (WPF) и `v2rayN.Desktop` (Avalonia). Цель — Avalonia +
`ServiceLib`.

---

## Что уже сделано (этап 1 — Kill Switch)

Реализован системный kill switch на Windows Firewall. Новые файлы:

```
ServiceLib/Manager/KillSwitch/
├── IKillSwitchService.cs     # интерфейс бэкенда (Arm/Disarm/Recover)
├── KillSwitchManager.cs      # синглтон-точка входа, выбор платформы
├── KillSwitchWindows.cs      # реализация через Windows Firewall (NetSecurity)
├── KillSwitchNoop.cs         # заглушка для Linux/macOS
├── KillSwitchContext.cs      # что разрешить, пока замок активен
└── KillSwitchState.cs        # состояние на диске для восстановления после сбоя
```

Точки интеграции (изменённые файлы):

| Файл | Что добавлено |
|------|---------------|
| `ServiceLib/Models/Configs/ConfigItems.cs` | класс `KillSwitchItem` (настройки) |
| `ServiceLib/Models/Configs/Config.cs` | поле `KillSwitchItem` |
| `ServiceLib/Handler/ConfigHandler.cs` | инициализация по умолчанию |
| `ServiceLib/Manager/CoreManager.cs` | **arm** после успешного TUN-подключения |
| `ServiceLib/Manager/AppManager.cs` | **disarm** при выходе из приложения |
| `ServiceLib/ViewModels/MainWindowViewModel.cs` | **recover** при старте |

### Как работает замок

Модель «запрет по умолчанию + белый список» (как у WireGuard):

1. Сохраняем текущий `DefaultOutboundAction` каждого профиля фаервола в файл
   состояния.
2. Добавляем группу Outbound/Allow-правил `v2rayN-KillSwitch`:
   - процесс ядра (`sing-box.exe`) — единственный, кто ходит в сеть напрямую
     (именно он дозванивается до VPN-сервера);
   - loopback (`127.0.0.1` / `::1`) — локальные прокси-порты и связь UI↔ядро;
   - DHCP (UDP 67/68) — чтобы физический адаптер сохранял аренду;
   - опционально: LAN, bypass-приложения и bypass-подсети (split tunneling).
3. Переключаем `DefaultOutboundAction` → `Block` на всех профилях.

Итог: в TUN-режиме наружу на физический интерфейс ходит только ядро; весь
остальной трафик (включая IPv6 и прямой DNS на :53) отбрасывается → утечки
невозможны ни при падении ядра, ни в момент подключения/отключения.

Жизненный цикл:
- **Arm** — в `CoreManager.LoadCore` после удачного старта, если профиль в TUN
  и `KillSwitchItem.EnableKillSwitch`. Идемпотентно: reload переподтверждает
  правила без «окна утечки».
- **Fail-closed** — внутренний `CoreStop` при reload замок **не снимает**, так
  что падение ядра в середине сессии держит сеть закрытой до переподключения.
- **Disarm** — при выходе из приложения (`AppExitAsync`) и из будущей кнопки
  Disconnect: иначе выход оставил бы ПК без интернета.
- **Recover** — при старте проверяется файл состояния; если прошлый запуск
  упал с активным замком, сеть разблокируется, чтобы не «окирпичить» ПК.

### Известное ограничение и ручная разблокировка

Запрет-по-умолчанию отбрасывает только трафик, не подошедший ни под одно
правило. Теоретически **уже существующее пользовательское Allow-правило** для
другой программы может пропустить её напрямую. В TUN-режиме это почти не играет
роли (приложения отдают пакеты в TUN-устройство, а не в прямой сокет — наружу
дозванивается только ядро), но для полной герметичности см. этап 1b ниже.

Аварийная ручная разблокировка (PowerShell от админа):
```powershell
Set-NetFirewallProfile -All -DefaultOutboundAction Allow
Remove-NetFirewallRule -Group 'v2rayN-KillSwitch' -ErrorAction SilentlyContinue
```

---

## Этап 1b — Нативный WFP-бэкенд (упрочнение)

Чтобы замок перекрывал даже чужие Allow-правила и авто-снимался при падении
процесса — портировать подход WireGuard-for-Windows (пакет `firewall`):
- динамическая WFP-сессия (`FWPM_SESSION_FLAG_DYNAMIC` → фильтры удаляются
  автоматически при закрытии хэндла/выходе процесса — защита от «кирпича»);
- собственный sublayer с высоким весом → permit/block поверх всех обычных
  правил фаервола;
- permit-фильтры: app-id ядра, loopback, DHCP, (опц.) LAN; block-фильтр на всё
  остальное (IPv4+IPv6) на уровне ALE.

Реализуется как второй `IKillSwitchService` (`KillSwitchWindowsWfp`) — callers
не меняются, переключение в `KillSwitchManager`. Это ~400–600 строк P/Invoke
(FwpmEngineOpen0/SublayerAdd0/FilterAdd0 + структуры FWPM). Рекомендую делать и
отлаживать уже на целевой Windows-машине (точность marshalling структур).

---

## Этап 2 — Split tunneling (приложения/подсети)

Две стороны, обе нужны:

1. **Маршрутизация (sing-box)** — чтобы bypass-трафик реально шёл мимо туннеля.
   В `ServiceLib/Services/CoreConfig/Singbox/SingboxRoutingService.cs` добавить
   route-правила с `outbound: "direct"` по:
   - `process_name` (имена exe из `KillSwitchItem.BypassApps`);
   - `ip_cidr` (подсети из `KillSwitchItem.BypassSubnets`).
   Режимы как у WireSock: «туннелировать только выбранные» vs «туннелировать всё
   кроме выбранных» — добавить `ESplitTunnelMode` в `KillSwitchItem`.
2. **Фаервол** — bypass-приложения/подсети уже прокидываются в
   `KillSwitchContext` и получают Allow-правила, так что при включённом замке
   они не блокируются. (Готово со стороны kill switch.)

Примечание: `process_name`-правила корректно работают на ядре **sing-box** (в
v2rayN TUN и так на sing-box), что подтверждается обсуждениями апстрима.

---

## Этап 3 — Рескин интерфейса под WireSock (Avalonia)

Цель фронтенда `v2rayN.Desktop`:
- один главный экран с крупным тумблером **Connect/Disconnect** и статусом
  (Connected / Connecting / Locked), индикатор kill switch и текущего IP;
- список профилей (import/export/delete), поиск/пинг;
- экран настроек: Kill Switch (вкл + Allow LAN), Split Tunneling (списки
  приложений и подсетей, режим), DNS, MTU.

Что переиспользовать: `MainWindowViewModel` (reload/connect), `ProfilesViewModel`,
`StatusBarViewModel`. Добавить команду **Disconnect** (сейчас у v2rayN явной нет)
→ `CoreManager.CoreStop()` + `KillSwitchManager.DisarmAsync()`.

Дизайн-экраны можно собрать отдельно (Claude Design) и затем переложить в
Avalonia axaml — скажите, если нужно.

---

## Этап 4 (опционально) — Разделение службы и UI

Фирменная черта WireSock: туннель живёт в фоновой службе, UI можно закрыть.
В v2rayN ядро привязано к процессу приложения. Это самый крупный рефакторинг:
вынести управление ядром + kill switch в Windows-службу, UI общается с ней по
named pipe/gRPC. Делать в последнюю очередь; на поведение kill switch этапа 1
не влияет.

---

## Этап 5 — Android (Avalonia + общий ServiceLib)

Ветка `android`, проект `v2rayN/v2rayN.Android` (net10.0-android, Avalonia.Android).
Подробности, сборка и ограничения — [docs/ANDROID.md](docs/ANDROID.md).

- VPN: `SecureVpnService` (VpnService) даёт TUN, ядро работает локальным прокси
  через тот же `CoreManager`, пакеты в SOCKS переводит hev-socks5-tunnel (P/Invoke).
- Kill switch — системный («Постоянная VPN» + «Блокировать без VPN»); приложение
  показывает его состояние и открывает настройки.
- Split tunneling — `KillSwitchItem.AndroidApps` (пакеты) и общий `BypassSubnets`,
  режим — общий `SplitTunnelWhitelist`; на Android приложения и подсети
  сочетаются через И.
- Общий код: `PlatformHost` (каталог данных, путь к ядру, допуск запуска ядра) —
  на ПК не задан, поведение не меняется.
- Сборка: `.github/workflows/build-android.yml` или `build-android.ps1`.

Дальше по Android: редактор правил маршрутизации, сканер QR камерой, редактор
профиля, иконки приложений в выборе, плитка быстрых настроек.

---

## Сборка и проверка (Windows, с правами администратора)

```powershell
# из каталога v2rayN/
dotnet build v2rayN.sln -c Release
# запускать от администратора (TUN + фаервол требуют прав)
```

Проверка kill switch:
1. Включить в настройках Kill Switch, выбрать TUN-профиль, подключиться.
2. `Get-NetFirewallProfile -All | ft Name,DefaultOutboundAction` → все `Block`.
3. `Get-NetFirewallRule -Group 'v2rayN-KillSwitch'` → правила на месте.
4. Снаружи туннеля (напр. убить `sing-box.exe`) — интернет пропадает, утечки нет.
5. Disconnect / выход → политика и правила восстановлены.
6. Симулировать краш (завершить приложение в диспетчере с активным замком) →
   следующий запуск разблокирует сеть (recover).

## Как завести форк на GitHub

1. Сделать форк `2dust/v2rayN` в своём аккаунте (кнопка Fork).
2. Склонировать, создать ветку `secure-connect`.
3. Применить патч: `git am secure-connect-killswitch.patch`
   (или `git apply` для варианта без commit-сообщения).
4. Собрать и проверить по шагам выше.

---

_Этап 1 (Kill Switch) реализован. Дальше по приоритету: 2 (split tunneling) и 3
(рескин UI). Нативный WFP (1b) и разделение службы (4) — упрочнение/«про»-версия._
