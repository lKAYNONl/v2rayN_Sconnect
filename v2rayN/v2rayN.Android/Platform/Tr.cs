namespace v2rayN.Mobile.Platform;

/// <summary>
/// Strings of the Android UI (Russian / English). Stock texts shared with the
/// desktop (dialogs, server messages) still come from ServiceLib's ResUI.
/// The language follows UiItem.CurrentLanguage; on first run it is taken
/// from the phone's locale.
/// </summary>
internal static class Tr
{
    public static bool Ru { get; private set; }

    public static void Init(string? language)
        => Ru = language?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true;

    private static string S(string ru, string en) => Ru ? ru : en;

    // Navigation
    public static string TabConnect => S("Подключение", "Connect");
    public static string TabProfiles => S("Профили", "Profiles");
    public static string TabSettings => S("Настройки", "Settings");

    // Home
    public static string StateOff => S("ОТКЛЮЧЕНО", "DISCONNECTED");
    public static string StateConnecting => S("ПОДКЛЮЧЕНИЕ…", "CONNECTING…");
    public static string StateStopping => S("ОТКЛЮЧЕНИЕ…", "DISCONNECTING…");
    public static string StateProtected => S("ЗАЩИЩЕНО", "PROTECTED");
    public static string StateSplit => S("РАЗДЕЛЬНЫЙ ТУННЕЛЬ", "SPLIT TUNNEL");
    public static string StateLockedSuffix => S(" · БЛОКИРОВКА", " · LOCKED");
    public static string NoProfile => S("Профиль не выбран", "No profile selected");
    public static string DialConnect => S("ПОДКЛЮЧИТЬ", "CONNECT");
    public static string Core => S("ядро", "core");
    public static string Mbps => S("Мбит/с", "Mbps");
    public static string KillLocked => S("Без VPN — блокировка", "Locked without VPN");
    public static string KillOffConnected => S("Kill switch выкл.", "Kill switch off");
    public static string KillSystem => S("Kill switch: Android", "Kill switch: Android");
    public static string ChooseProfileHint => S("Нажмите, чтобы выбрать профиль", "Tap to choose a profile");

    // Profiles
    public static string Profiles => S("ПРОФИЛИ", "PROFILES");
    public static string Import => S("Импорт", "Import");
    public static string AddSubscription => S("Подписка", "Subscription");
    public static string UpdateSubs => S("Обновить", "Update");
    public static string PingAll => S("Пинг", "Ping");
    public static string Search => S("Поиск", "Search");
    public static string EmptyProfiles => S(
        "Профилей пока нет. Скопируйте ссылку (vless://, vmess://, ss://, trojan://, hy2://…) или адрес подписки и нажмите «Импорт».",
        "No profiles yet. Copy a share link (vless://, vmess://, ss://, trojan://, hy2://…) or a subscription URL and tap Import.");
    public static string MenuPing => S("Пинг", "Ping");
    public static string MenuCopyLink => S("Копировать ссылку", "Copy link");
    public static string MenuDelete => S("Удалить", "Delete");
    public static string SubscriptionUrl => S("Адрес подписки", "Subscription URL");
    public static string SubscriptionName => S("Название (необязательно)", "Name (optional)");
    public static string Add => S("Добавить", "Add");
    public static string Cancel => S("Отмена", "Cancel");
    public static string Ok => S("ОК", "OK");
    public static string Yes => S("Да", "Yes");
    public static string No => S("Нет", "No");
    public static string SubscriptionAdded => S("Подписка добавлена, обновляю…", "Subscription added, updating…");
    public static string BadUrl => S("Нужен адрес https://…", "An https://… URL is required");
    public static string Updating => S("Обновляю подписки…", "Updating subscriptions…");
    public static string Testing => S("Проверяю задержку…", "Testing latency…");

    // Settings: kill switch
    public static string SectionKill => S("KILL SWITCH", "KILL SWITCH");
    public static string KillTitle => S("Системный kill switch", "System kill switch");
    public static string KillText => S(
        "На Android трафик без VPN блокирует сама система. Откройте настройки VPN, выберите Secure Connect (шестерёнка) и включите «Постоянная VPN» и «Блокировать подключения без VPN».",
        "On Android the system blocks traffic outside the VPN. Open the VPN settings, tap the gear next to Secure Connect and turn on “Always-on VPN” and “Block connections without VPN”.");
    public static string OpenVpnSettings => S("Открыть настройки VPN", "Open VPN settings");
    public static string KillStatusUnknown => S("Состояние видно во время подключения.", "The state is shown while connected.");
    public static string KillStatus(bool alwaysOn, bool lockdown) => S(
        $"Сейчас: постоянная VPN — {(alwaysOn ? "вкл" : "выкл")}, блокировка без VPN — {(lockdown ? "вкл" : "выкл")}.",
        $"Now: always-on VPN {(alwaysOn ? "on" : "off")}, block without VPN {(lockdown ? "on" : "off")}.");
    public static string AllowLan => S("Доступ к локальной сети", "Allow local network");
    public static string AllowLanText => S(
        "Роутер, принтеры, NAS и другие устройства LAN — напрямую, мимо туннеля.",
        "Router, printers, NAS and other LAN devices are reached directly, outside the tunnel.");

    // Settings: split tunneling
    public static string SectionSplit => S("РАЗДЕЛЬНОЕ ТУННЕЛИРОВАНИЕ", "SPLIT TUNNELING");
    public static string ModeWhitelist => S("Только выбранное", "Only selected");
    public static string ModeBypass => S("Всё, кроме выбранного", "All except selected");
    public static string WhitelistText => S(
        "Через туннель идут только выбранные приложения и подсети. Пустые списки — туннелируется всё устройство.",
        "Only the selected apps and subnets use the tunnel. Empty lists tunnel the whole device.");
    public static string BypassText => S(
        "Выбранные приложения и подсети идут напрямую, всё остальное — через туннель.",
        "The selected apps and subnets go direct, everything else uses the tunnel.");
    public static string SplitAndText => S(
        "Приложения и подсети действуют вместе: в туннель попадает трафик выбранных приложений к выбранным подсетям. С «Блокировать подключения без VPN» всё, что вне туннеля, остаётся без сети.",
        "Apps and subnets combine: the tunnel carries the selected apps' traffic to the selected subnets. With “Block connections without VPN” anything outside the tunnel has no network.");
    public static string Apps => S("Приложения", "Apps");
    public static string Subnets => S("Подсети", "Subnets");
    public static string Choose => S("Выбрать…", "Choose…");
    public static string SelectedCount(int n) => S($"Выбрано: {n}", $"Selected: {n}");
    public static string NoneSelected => S("Ничего не выбрано", "Nothing selected");
    public static string SubnetHint => S("например 10.0.0.0/8", "e.g. 10.0.0.0/8");
    public static string BadSubnet(string s) => S($"Неверная подсеть: {s}", $"Not a valid subnet: {s}");
    public static string AppliedOnReconnect => S("Применено", "Applied");

    // Settings: routes & app
    public static string SectionRoutes => S("МАРШРУТЫ", "ROUTES");
    public static string RoutingRules => S("Правила маршрутизации", "Routing rules");
    public static string RulesCount(int n) => S($"правил: {n}", $"{n} rules");
    public static string SectionApp => S("ПРИЛОЖЕНИЕ", "APP");
    public static string Language => S("Язык", "Language");
    public static string RestartForLanguage => S("Язык сменится после перезапуска приложения", "The language changes after an app restart");
    public static string Log => S("Журнал", "Log");
    public static string LogText => S("Вывод ядра и шаги подключения — пригодится для отчёта об ошибке.", "Core output and connection steps - useful for bug reports.");
    public static string Version(string v) => S($"Версия {v}", $"Version {v}");

    // Sheets
    public static string Done => S("Готово", "Done");
    public static string SearchApps => S("Поиск приложений", "Search apps");
    public static string ShowSystem => S("Системные", "System apps");
    public static string Loading => S("Загрузка…", "Loading…");
    public static string Routes => S("Маршруты", "Routes");
    public static string RoutingSet => S("Набор правил", "Rule set");
    public static string Presets => S("Пресеты", "Presets");
    public static string MoveUp => S("Выше", "Move up");
    public static string MoveDown => S("Ниже", "Move down");
    public static string RulesHint => S(
        "Правила проверяются сверху вниз. Создавать и редактировать правила пока можно в версии для ПК — набор правил общий.",
        "Rules match top to bottom. Creating and editing rules is in the desktop version for now - the rule set is shared.");
    public static string Copy => S("Копировать", "Copy");
    public static string Clear => S("Очистить", "Clear");
    public static string Copied => S("Скопировано", "Copied");
    public static string Back => S("Назад", "Back");

    // Connection & notifications
    public static string VpnDenied => S("Без разрешения на VPN подключиться нельзя", "VPN permission is required to connect");
    public static string CoreFailed => S("Ядро не запустилось — подробности в журнале", "The core did not start - see the log");
    public static string TunFailed(string why) => S($"Не удалось создать VPN-интерфейс: {why}", $"Could not create the VPN interface: {why}");
    public static string NotPrepared => S("нет разрешения на VPN", "VPN permission missing");
    public static string Revoked => S("VPN отключён системой или другим приложением", "The VPN was turned off by the system or another app");
    public static string NotificationChannel => S("VPN-подключение", "VPN connection");
    public static string NotifConnecting => S("Подключение…", "Connecting…");
    public static string NotifConnected(string server) => S($"Подключено: {server}", $"Connected: {server}");
    public static string Disconnect => S("Отключить", "Disconnect");
    public static string NotOnAndroid => S("В Android-версии этого пока нет", "Not available on Android yet");
    public static string StartupFailed => S("Не удалось загрузить настройки — подробности в журнале", "Could not load the settings - see the log");
}
