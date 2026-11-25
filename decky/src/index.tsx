import {
  ButtonItem,
  DialogButton,
  DropdownItem,
  Field,
  Focusable,
  ModalRoot,
  PanelSection,
  PanelSectionRow,
  TextField,
  ToggleField,
  showModal,
  staticClasses,
} from "@decky/ui";
import { callable, definePlugin, toaster } from "@decky/api";
import { CSSProperties, ReactNode, useEffect, useRef, useState } from "react";
import { FaChevronDown, FaChevronRight, FaFolder, FaRegStar, FaShieldAlt, FaStar } from "react-icons/fa";

interface Profile {
  id: string;
  name: string;
  type: string;
  address: string;
  port: number;
  // the subscription the profile belongs to ("" - none)
  group: string;
  groupId?: string;
  favorite?: boolean;
}

interface Group {
  id: string;
  name: string;
}

interface ProfilesAnswer {
  ok: boolean;
  error?: string;
  active?: string;
  selected?: string;
  profiles?: Profile[];
  // subscriptions that have profiles, in the order of the app's list
  groups?: Group[];
  // favourite profile ids, in the order they were starred
  favorites?: string[];
}

interface Status {
  running: boolean;
  busy: boolean;
  profile_id: string;
  profile_name: string;
  error: string;
  verbose?: boolean;
}

type Mode = "all" | "only" | "except";

interface SplitApp {
  value: string;
  label: string;
  kind: string;
}

interface Split {
  ok: boolean;
  // false: the change was not saved (the settings shown are the old ones)
  applied?: boolean;
  error?: string;
  mode?: Mode;
  routing?: boolean;
  apps?: SplitApp[];
  subnets?: number;
}

interface SplitChange {
  mode?: Mode;
  routing?: boolean;
  add?: string[];
  remove?: string[];
  labels?: Record<string, string>;
}

interface SplitAnswer {
  split: Split;
  status: Status;
}

interface Program {
  value: string; // group key
  values: string[]; // the split tunneling entries that match it
  label: string;
  title: string;
  kind: string;
  detail: string;
  games: string[];
  network: boolean;
  count: number;
}

interface DiagAnswer {
  ok: boolean;
  verdicts: string[];
  report: string;
  proxy_leftover: boolean;
}

interface ResetAnswer {
  ok: boolean;
  message: string;
}

interface ProgramsAnswer {
  ok: boolean;
  error?: string;
  programs?: Program[];
}

const getProfiles = callable<[], ProfilesAnswer>("get_profiles");
const setFavorite = callable<[string, boolean], ProfilesAnswer>("set_favorite");
const selectProfile = callable<[string], Status>("select_profile");
const getStatus = callable<[], Status>("get_status");
const connect = callable<[string], Status>("connect");
const disconnect = callable<[], Status>("disconnect");
const getSplit = callable<[], Split>("get_split");
const updateSplit = callable<[SplitChange], SplitAnswer>("update_split");
const getPrograms = callable<[], ProgramsAnswer>("get_programs");
const diagnose = callable<[], DiagAnswer>("diagnose");
const setVerbose = callable<[boolean], Status>("set_verbose");
const resetSystemProxy = callable<[], ResetAnswer>("reset_system_proxy");

const MODES: { data: Mode; label: string }[] = [
  { data: "all", label: "Весь Deck" },
  { data: "only", label: "Только выбранные" },
  { data: "except", label: "Всё, кроме выбранных" },
];

const MODE_HINT: Record<Mode, string> = {
  all: "Через туннель идёт весь трафик Deck — игры Steam, сторонние игры и приложения.",
  only: "Через туннель идут только приложения из списка, остальное — напрямую.",
  except: "Приложения из списка идут напрямую, всё остальное — через туннель.",
};

const MODE_STATUS: Record<Mode, string> = {
  all: "Весь Deck через туннель",
  only: "Только выбранные приложения",
  except: "Всё, кроме выбранных",
};

const SHOWN_PROGRAMS = 60;

// Text sizes for the Deck's screen: the stock descriptions (12px, grey) are hard
// to read from arm's length, so the plugin's own texts are larger and brighter.
const S: Record<string, CSSProperties> = {
  hint: { fontSize: 13, lineHeight: "18px", color: "#c3c8cf", whiteSpace: "normal" },
  state: { display: "flex", alignItems: "center", gap: 8, fontSize: 19, lineHeight: "24px", fontWeight: 700 },
  dot: { width: 12, height: 12, borderRadius: 6, flex: "none" },
  name: { fontSize: 15, lineHeight: "20px", fontWeight: 600, marginTop: 6, whiteSpace: "normal", wordBreak: "break-word" },
  sub: { fontSize: 13, lineHeight: "18px", opacity: 0.75, marginTop: 2, whiteSpace: "normal", wordBreak: "break-word" },
  // a profile as a two-line button (the name wraps instead of being cut)
  card: {
    flex: 1,
    minWidth: 0,
    height: "auto",
    minHeight: 44,
    padding: "8px 12px",
    textAlign: "left",
    display: "flex",
    flexDirection: "column",
    alignItems: "flex-start",
    justifyContent: "center",
    border: "2px solid transparent",
  },
  cardChosen: { border: "2px solid #4f8cff" },
  cardName: { fontSize: 15, lineHeight: "20px", fontWeight: 600, whiteSpace: "normal", wordBreak: "break-word", textAlign: "left" },
  cardSub: { fontSize: 12, lineHeight: "16px", opacity: 0.7, whiteSpace: "normal", wordBreak: "break-word", textAlign: "left" },
  star: {
    flex: "none",
    width: 56,
    minWidth: 0,
    height: "auto",
    padding: 0,
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
  },
  folder: {
    height: "auto",
    minHeight: 46,
    padding: "8px 12px",
    display: "flex",
    alignItems: "center",
    gap: 10,
    textAlign: "left",
  },
  folderName: { flex: 1, minWidth: 0, fontSize: 17, lineHeight: "22px", fontWeight: 700, whiteSpace: "normal", wordBreak: "break-word", textAlign: "left" },
  folderCount: { flex: "none", fontSize: 14, opacity: 0.7 },
  modalTitle: { fontSize: 24, lineHeight: "30px", fontWeight: 700 },
  modalList: { display: "flex", flexDirection: "column", gap: 8, maxHeight: "62vh", overflowY: "auto", padding: "4px 6px 4px 2px", marginTop: 12 },
};

const GOLD = "#f5c451";

function profileName(p: Profile): string {
  return p.name || `${p.address}:${p.port}`;
}

/** The profiles as the app's list shows them: favourites, profiles without a
 *  subscription, then every subscription as a folder. A favourite is listed only
 *  among the favourites. */
interface Folder {
  id: string;
  name: string;
  profiles: Profile[];
}

interface Tree {
  favorites: Profile[];
  loose: Profile[];
  folders: Folder[];
}

function buildTree(answer: ProfilesAnswer | null): Tree {
  const profiles = answer?.profiles ?? [];
  const order = answer?.favorites ?? [];
  const favorites = profiles
    .filter((p) => p.favorite)
    .sort((a, b) => order.indexOf(a.id) - order.indexOf(b.id));
  const rest = profiles.filter((p) => !p.favorite);
  const groups = answer?.groups ?? [];
  const known = new Set(groups.map((g) => g.id));
  const folders = groups
    .map((g) => ({ id: g.id, name: g.name || "Подписка", profiles: rest.filter((p) => p.groupId === g.id) }))
    .filter((f) => f.profiles.length > 0);
  return { favorites, loose: rest.filter((p) => !p.groupId || !known.has(p.groupId)), folders };
}

const FAVORITES_FOLDER = "*favorites";
const COLLAPSED_KEY = "secure-connect.collapsed-folders";

function loadCollapsed(): string[] {
  try {
    const value: unknown = JSON.parse(window.localStorage.getItem(COLLAPSED_KEY) ?? "[]");
    return Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : [];
  } catch {
    return [];
  }
}

function saveCollapsed(ids: string[]) {
  try {
    window.localStorage.setItem(COLLAPSED_KEY, JSON.stringify(ids));
  } catch {
    // not remembered, that is all
  }
}

function Hint(props: { children: ReactNode; focusable?: boolean }) {
  // focusable: a text at the end of a section can be scrolled to with the gamepad
  return (
    <PanelSectionRow>
      <Field focusable={props.focusable ?? false} bottomSeparator="none" description={<div style={S.hint}>{props.children}</div>} />
    </PanelSectionRow>
  );
}

interface ProfileCardProps {
  profile: Profile;
  chosen: boolean;
  connected: boolean;
  disabled?: boolean;
  indent?: boolean;
  large?: boolean;
  onPick: (p: Profile) => void;
  // with it the card gets a star button (and X on the gamepad)
  onStar?: (p: Profile) => void;
}

function ProfileCard(props: ProfileCardProps) {
  const p = props.profile;
  const large = props.large === true;
  let note = `${p.type} · ${p.address}`;
  if (props.connected) {
    note = `● подключён · ${note}`;
  } else if (props.chosen) {
    note = `● выбран · ${note}`;
  }
  const card = (
    <DialogButton
      style={{ ...S.card, ...(props.chosen ? S.cardChosen : {}), ...(large ? { minHeight: 58, padding: "10px 14px" } : {}) }}
      disabled={props.disabled}
      onClick={() => props.onPick(p)}
    >
      <div style={{ ...S.cardName, ...(large ? { fontSize: 18, lineHeight: "24px" } : {}) }}>{profileName(p)}</div>
      <div style={{ ...S.cardSub, ...(large ? { fontSize: 14, lineHeight: "18px" } : {}) }}>{note}</div>
    </DialogButton>
  );
  const onStar = props.onStar;
  if (!onStar) {
    return card;
  }
  const starLabel = p.favorite ? "Убрать из избранного" : "В избранное";
  return (
    <Focusable
      flow-children="horizontal"
      style={{ display: "flex", gap: 8, marginLeft: props.indent ? 22 : 0 }}
      onSecondaryButton={() => onStar(p)}
      onSecondaryActionDescription={starLabel}
    >
      {card}
      <DialogButton style={S.star} disabled={props.disabled} onClick={() => onStar(p)}>
        {p.favorite ? <FaStar size={24} color={GOLD} /> : <FaRegStar size={24} />}
      </DialogButton>
    </Focusable>
  );
}

interface ProfilesModalProps {
  // set by showModal
  closeModal?: () => void;
  initial: ProfilesAnswer | null;
  chosen: string;
  connected: string;
  onPick: (id: string) => void;
  onChanged: (answer: ProfilesAnswer) => void;
}

/** All profiles on a full-size page: favourites, profiles without a subscription
 *  and the subscriptions as folders. A - choose, X (or the star) - favourite. */
function ProfilesModal(props: ProfilesModalProps) {
  const [answer, setAnswer] = useState<ProfilesAnswer | null>(props.initial);
  const [collapsed, setCollapsed] = useState<string[]>(loadCollapsed);
  const [busy, setBusy] = useState<boolean>(false);
  const [error, setError] = useState<string>("");

  const tree = buildTree(answer);
  const empty = tree.favorites.length + tree.loose.length + tree.folders.length === 0;

  const run = async (action: () => Promise<ProfilesAnswer>) => {
    setBusy(true);
    try {
      const next = await action();
      if (next.ok) {
        setAnswer(next);
        setError("");
        props.onChanged(next);
      } else {
        setError(next.error ?? "Не удалось выполнить.");
      }
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy(false);
    }
  };

  const toggleFolder = (id: string) => {
    const next = collapsed.includes(id) ? collapsed.filter((v) => v !== id) : [...collapsed, id];
    setCollapsed(next);
    saveCollapsed(next);
  };

  const pick = (p: Profile) => {
    props.onPick(p.id);
    props.closeModal?.();
  };

  const star = (p: Profile) => {
    if (!busy) {
      void run(() => setFavorite(p.id, !p.favorite));
    }
  };

  const cards = (profiles: Profile[], indent: boolean) =>
    profiles.map((p) => (
      <ProfileCard
        key={p.id}
        profile={p}
        chosen={p.id === props.chosen}
        connected={p.id === props.connected}
        indent={indent}
        large={true}
        onPick={pick}
        onStar={star}
      />
    ));

  const folder = (id: string, name: string, icon: ReactNode, profiles: Profile[]) => {
    const open = !collapsed.includes(id);
    return (
      <div key={id} style={{ display: "flex", flexDirection: "column", gap: 8 }}>
        <DialogButton style={S.folder} onClick={() => toggleFolder(id)}>
          {open ? <FaChevronDown size={14} /> : <FaChevronRight size={14} />}
          {icon}
          <div style={S.folderName}>{name}</div>
          <div style={S.folderCount}>{profiles.length}</div>
        </DialogButton>
        {open ? cards(profiles, true) : null}
      </div>
    );
  };

  return (
    <ModalRoot closeModal={props.closeModal} onCancel={props.closeModal} bAllowFullSize={true}>
      <div style={S.modalTitle}>Профили</div>
      <div style={{ ...S.hint, marginTop: 4 }}>
        Нажмите на профиль, чтобы выбрать его. Звёздочка (или кнопка X) — в избранное: такие профили всегда вверху.
      </div>
      {error ? <div style={{ ...S.hint, marginTop: 6, color: "#ff8a80" }}>{error}</div> : null}
      <div style={S.modalList}>
        {empty ? <div style={S.hint}>Профилей нет — добавьте их в приложении (Desktop Mode).</div> : null}
        {tree.favorites.length > 0
          ? folder(FAVORITES_FOLDER, "Избранное", <FaStar size={18} color={GOLD} />, tree.favorites)
          : null}
        {cards(tree.loose, false)}
        {tree.folders.map((f) => folder(f.id, f.name, <FaFolder size={18} />, f.profiles))}
        <DialogButton disabled={busy} onClick={() => void run(() => getProfiles())}>
          {busy ? "Загрузка…" : "Обновить список"}
        </DialogButton>
      </div>
    </ModalRoot>
  );
}

function entryKind(kind: string, value: string): string {
  switch (kind) {
    case "proton":
      return "Proton / Wine — все игры на этой версии";
    case "folder":
      return `Папка: ${value}`;
    case "flatpak":
      return `Flatpak: ${value}`;
    case "name":
      return "По имени файла";
    default:
      return value;
  }
}

function programDetail(p: Program): string {
  const net = p.network ? "● в сети · " : "";
  switch (p.kind) {
    case "proton":
      return p.values.every((v) => v.endsWith("/"))
        ? `${net}Proton / Wine «${p.title}»: добавится вся версия — все игры на ней`
        : `${net}Wine: ${p.detail} — все программы на этом Wine`;
    case "interpreter":
      return `${net}Интерпретатор ${p.detail}: совпадут все программы на нём`;
    case "flatpak":
      return `${net}Flatpak · ${p.detail}`;
    case "appimage":
      return `${net}AppImage · по имени файла`;
    case "steam":
      return `${net}Клиент Steam · ${p.detail}`;
    default:
      return `${net}${p.detail}`;
  }
}

function programMatches(p: Program, query: string): boolean {
  if (!query) {
    return true;
  }
  return [p.label, p.title, p.detail, ...p.games].some((t) => t.toLowerCase().includes(query));
}

function Content() {
  const [answer, setAnswer] = useState<ProfilesAnswer | null>(null);
  const [selected, setSelected] = useState<string>("");
  const [listError, setListError] = useState<string>("");
  const [loading, setLoading] = useState<boolean>(false);
  const [status, setStatus] = useState<Status | null>(null);
  const [working, setWorking] = useState<boolean>(false);

  const [split, setSplit] = useState<Split | null>(null);
  const [picking, setPicking] = useState<boolean>(false);
  const [programs, setPrograms] = useState<Program[] | null>(null);
  const [programsError, setProgramsError] = useState<string>("");
  const [query, setQuery] = useState<string>("");
  const [chosen, setChosen] = useState<string[]>([]);

  const [diag, setDiag] = useState<DiagAnswer | null>(null);
  const [checking, setChecking] = useState<boolean>(false);
  const [showCheck, setShowCheck] = useState<boolean>(false);

  // The profiles page lives outside this component and outlives its renders:
  // it reads the current state through this.
  const live = useRef({ running: false, connected: "" });

  const loadProfiles = async () => {
    setLoading(true);
    try {
      const next = await getProfiles();
      if (next.ok) {
        const list = next.profiles ?? [];
        setAnswer(next);
        setListError(list.length === 0 ? "Профилей нет — добавьте их в приложении (Desktop Mode)." : "");
        setSelected((current) => current || next.selected || next.active || (list.length > 0 ? list[0].id : ""));
      } else {
        setListError(next.error ?? "Не удалось получить профили.");
      }
    } catch (e) {
      setListError(String(e));
    } finally {
      setLoading(false);
    }
  };

  const loadSplit = async () => {
    try {
      setSplit(await getSplit());
    } catch (e) {
      setSplit({ ok: false, error: String(e) });
    }
  };

  const refreshStatus = async () => {
    try {
      setStatus(await getStatus());
    } catch {
      // the backend is restarting; the next tick will catch up
    }
  };

  useEffect(() => {
    void loadProfiles();
    void loadSplit();
    void refreshStatus();
    const timer = window.setInterval(() => void refreshStatus(), 3000);
    return () => window.clearInterval(timer);
  }, []);

  const showError = (error: string) => {
    if (error) {
      toaster.toast({ title: "Secure Connect", body: error });
    }
  };

  const apply = async (action: () => Promise<Status>) => {
    setWorking(true);
    try {
      const next = await action();
      setStatus(next);
      // A profile that failed to start leaves the previous session (if any) up.
      if (next.running && next.profile_id) {
        setSelected(next.profile_id);
      }
      showError(next.error);
    } catch (e) {
      showError(String(e));
    } finally {
      setWorking(false);
    }
  };

  // Split tunneling changes reconnect a running session right away.
  const changeSplit = async (change: SplitChange): Promise<boolean> => {
    setWorking(true);
    try {
      const answer = await updateSplit(change);
      setSplit(answer.split);
      setStatus(answer.status);
      const saved = answer.split.ok && answer.split.applied !== false;
      showError(saved ? answer.status.error : answer.split.error ?? "Не удалось сохранить.");
      return saved;
    } catch (e) {
      showError(String(e));
      return false;
    } finally {
      setWorking(false);
    }
  };

  const loadPrograms = async () => {
    setPrograms(null);
    setProgramsError("");
    try {
      const answer = await getPrograms();
      if (answer.ok) {
        setPrograms(answer.programs ?? []);
      } else {
        setProgramsError(answer.error ?? "Не удалось получить список процессов.");
        setPrograms([]);
      }
    } catch (e) {
      setProgramsError(String(e));
      setPrograms([]);
    }
  };

  const openPicker = () => {
    setQuery("");
    setChosen([]);
    setPicking(true);
    void loadPrograms();
  };

  const addChosen = async () => {
    const add: string[] = [];
    const labels: Record<string, string> = {};
    for (const p of programs ?? []) {
      if (chosen.includes(p.value)) {
        for (const v of p.values) {
          add.push(v);
          labels[v] = p.values.length > 1 ? `${p.label} (${v.split("/").pop()})` : p.label;
        }
      }
    }
    if (await changeSplit({ add, labels })) {
      setPicking(false);
      setChosen([]);
    }
  };

  const runCheck = async () => {
    setChecking(true);
    try {
      setDiag(await diagnose());
    } catch (e) {
      setDiag({ ok: false, verdicts: [String(e)], report: "", proxy_leftover: false });
    } finally {
      setChecking(false);
    }
  };

  const onResetProxy = async () => {
    try {
      const answer = await resetSystemProxy();
      showError(answer.message);
      if (answer.ok) {
        await runCheck();
      }
    } catch (e) {
      showError(String(e));
    }
  };

  const running = status?.running ?? false;
  const busy = working || (status?.busy ?? false);
  live.current = { running, connected: running ? status?.profile_id ?? "" : "" };
  const profiles = answer?.profiles ?? [];
  const current = profiles.find((p) => p.id === selected);
  const favorites = buildTree(answer).favorites;
  const mode: Mode = split?.mode ?? "all";
  const apps = split?.apps ?? [];
  const inList = new Set(apps.map((a) => a.value));

  const onToggle = (on: boolean) => {
    if (on) {
      if (selected) {
        void apply(() => connect(selected));
      }
    } else {
      void apply(() => disconnect());
    }
  };

  const onSelect = (id: string) => {
    setSelected(id);
    if (live.current.running) {
      // Switching profiles while connected reconnects to the new one.
      if (id !== live.current.connected) {
        void apply(() => connect(id));
      }
    } else {
      // Remembered for the next time the panel is opened.
      void selectProfile(id).catch(() => undefined);
    }
  };

  const openProfiles = () => {
    showModal(
      <ProfilesModal
        initial={answer}
        chosen={selected}
        connected={live.current.connected}
        onPick={onSelect}
        onChanged={setAnswer}
      />,
    );
  };

  let stateText = "Отключено";
  let stateColor = "#8b929a";
  if (busy) {
    stateText = running ? "Применение…" : "Подключение…";
    stateColor = GOLD;
  } else if (running) {
    stateText = "Подключено";
    stateColor = "#59bf40";
  }
  let nameText = loading ? "Загрузка профилей…" : "Профиль не выбран";
  if (running && status?.profile_name) {
    nameText = status.profile_name;
  } else if (current) {
    nameText = profileName(current);
  }
  let subText = current?.group ? `Подписка: ${current.group}` : "";
  if (running && split?.ok) {
    subText = MODE_STATUS[mode];
  }

  const q = query.trim().toLowerCase();
  const found = (programs ?? []).filter((p) => programMatches(p, q));
  const shown = found.slice(0, SHOWN_PROGRAMS);

  const subnets = split?.subnets ?? 0;
  let emptyHint = "";
  if (apps.length === 0) {
    if (mode === "only") {
      emptyHint =
        subnets > 0
          ? `Приложений в списке нет — через туннель идут только подсети из приложения (${subnets}).`
          : "Список пуст — пока через туннель идёт весь Deck. Запустите игру или приложение и добавьте его.";
    } else if (mode === "except") {
      emptyHint =
        subnets > 0
          ? `Приложений в списке нет — напрямую идут только подсети из приложения (${subnets}).`
          : "Список пуст — через туннель идёт всё.";
    }
  }

  return (
    <>
      <PanelSection>
        <PanelSectionRow>
          <div style={{ padding: "2px 0 4px" }}>
            <div style={S.state}>
              <div style={{ ...S.dot, background: stateColor }} />
              {stateText}
            </div>
            <div style={S.name}>{nameText}</div>
            {subText ? <div style={S.sub}>{subText}</div> : null}
          </div>
        </PanelSectionRow>
        <PanelSectionRow>
          <ToggleField
            label="VPN"
            checked={running}
            disabled={busy || (!running && !selected)}
            onChange={onToggle}
          />
        </PanelSectionRow>
        {status?.error ? (
          <PanelSectionRow>
            <Field label="Ошибка" description={<div style={{ ...S.hint, color: "#ff8a80" }}>{status.error}</div>} />
          </PanelSectionRow>
        ) : null}
        <PanelSectionRow>
          <ButtonItem layout="below" disabled={profiles.length === 0} onClick={openProfiles}>
            {loading && profiles.length === 0 ? "Загрузка…" : "Все профили и папки…"}
          </ButtonItem>
        </PanelSectionRow>
        {listError ? (
          <>
            <Hint>{listError}</Hint>
            <PanelSectionRow>
              <ButtonItem layout="below" disabled={loading} onClick={() => void loadProfiles()}>
                {loading ? "Загрузка…" : "Обновить список профилей"}
              </ButtonItem>
            </PanelSectionRow>
          </>
        ) : null}
      </PanelSection>

      {favorites.length > 0 ? (
        <PanelSection title="Избранное">
          {favorites.map((p) => (
            <PanelSectionRow key={p.id}>
              <ProfileCard
                profile={p}
                chosen={p.id === selected}
                connected={running && p.id === status?.profile_id}
                disabled={busy}
                onPick={(picked) => onSelect(picked.id)}
              />
            </PanelSectionRow>
          ))}
        </PanelSection>
      ) : null}

      <PanelSection title="Что идёт через туннель">
        {split && !split.ok ? (
          <>
            <PanelSectionRow>
              <Field label="Ошибка" description={split.error ?? "Не удалось прочитать настройки."} />
            </PanelSectionRow>
            <PanelSectionRow>
              <ButtonItem layout="below" onClick={() => void loadSplit()}>
                Повторить
              </ButtonItem>
            </PanelSectionRow>
          </>
        ) : (
          <>
            <PanelSectionRow>
              <DropdownItem
                label="Режим"
                layout="below"
                description={<div style={S.hint}>{MODE_HINT[mode]}</div>}
                rgOptions={MODES}
                selectedOption={mode}
                disabled={busy || !split}
                onChange={(option) => {
                  const next = option.data as Mode;
                  if (next !== mode) {
                    if (next === "all") {
                      setPicking(false);
                    }
                    void changeSplit({ mode: next });
                  }
                }}
              />
            </PanelSectionRow>
            <PanelSectionRow>
              <ToggleField
                label="Правила маршрутизации"
                description={
                  <div style={S.hint}>
                    {split?.routing ?? true
                      ? "Вкл: адреса из правил маршрутизации (например, локальные сайты) идут напрямую."
                      : "Выкл: весь трафик туннеля идёт через прокси, напрямую — только локальная сеть."}
                  </div>
                }
                checked={split?.routing ?? true}
                disabled={busy || !split}
                onChange={(on) => void changeSplit({ routing: on })}
              />
            </PanelSectionRow>

            {mode !== "all" ? (
              <>
                {apps.map((a) => (
                  <PanelSectionRow key={a.value}>
                    <ButtonItem
                      layout="below"
                      label={a.label}
                      description={entryKind(a.kind, a.value)}
                      disabled={busy}
                      onClick={() => void changeSplit({ remove: [a.value] })}
                    >
                      Убрать из списка
                    </ButtonItem>
                  </PanelSectionRow>
                ))}
                {emptyHint ? <Hint>{emptyHint}</Hint> : null}
                {apps.length > 0 && subnets > 0 ? <Hint>{`Ещё подсетей из приложения: ${subnets}.`}</Hint> : null}

                {!picking ? (
                  <PanelSectionRow>
                    <ButtonItem layout="below" disabled={busy || !split} onClick={openPicker}>
                      Добавить запущенное…
                    </ButtonItem>
                  </PanelSectionRow>
                ) : (
                  <>
                    <PanelSectionRow>
                      <TextField
                        label="Поиск по процессам"
                        value={query}
                        bShowClearAction={true}
                        onChange={(e) => setQuery(e.target.value)}
                      />
                    </PanelSectionRow>
                    {programs === null ? <Hint>Загрузка списка процессов…</Hint> : null}
                    {programsError ? (
                      <PanelSectionRow>
                        <Field label="Ошибка" description={programsError} />
                      </PanelSectionRow>
                    ) : null}
                    {programs !== null && found.length === 0 && !programsError ? (
                      <Hint>{q ? "Ничего не найдено." : "Нет подходящих процессов."}</Hint>
                    ) : null}
                    {shown.map((p) => {
                      const already = p.values.every((v) => inList.has(v));
                      return (
                        <PanelSectionRow key={p.value}>
                          <ToggleField
                            label={p.label}
                            description={already ? `Уже в списке · ${programDetail(p)}` : programDetail(p)}
                            checked={already || chosen.includes(p.value)}
                            disabled={already || busy}
                            onChange={(on) =>
                              setChosen((current) =>
                                on ? [...current.filter((v) => v !== p.value), p.value] : current.filter((v) => v !== p.value),
                              )
                            }
                          />
                        </PanelSectionRow>
                      );
                    })}
                    {found.length > shown.length ? (
                      <Hint>{`Показаны первые ${shown.length} из ${found.length} — уточните поиск.`}</Hint>
                    ) : null}
                    <PanelSectionRow>
                      <ButtonItem layout="below" disabled={busy || chosen.length === 0} onClick={() => void addChosen()}>
                        {chosen.length > 0 ? `Добавить выбранные (${chosen.length})` : "Отметьте процессы выше"}
                      </ButtonItem>
                    </PanelSectionRow>
                    <PanelSectionRow>
                      <ButtonItem layout="below" disabled={programs === null} onClick={() => void loadPrograms()}>
                        Обновить список процессов
                      </ButtonItem>
                    </PanelSectionRow>
                    <PanelSectionRow>
                      <ButtonItem layout="below" onClick={() => setPicking(false)}>
                        Закрыть
                      </ButtonItem>
                    </PanelSectionRow>
                  </>
                )}
                <Hint focusable={true}>
                  Игры на Proton добавляются вместе с версией Proton: в системе у них виден не .exe игры, а загрузчик
                  Wine. «● в сети» — процесс сейчас использует сеть.
                </Hint>
              </>
            ) : null}
          </>
        )}
        <Hint focusable={true}>
          Режим и список общие с приложением Secure Connect в режиме рабочего стола (кроме «Весь Deck» — он только для
          игрового режима).
        </Hint>
      </PanelSection>

      <PanelSection title="Проверка">
        <PanelSectionRow>
          <ButtonItem layout="below" onClick={() => setShowCheck(!showCheck)}>
            {showCheck ? "Скрыть проверку" : "Что-то не работает?"}
          </ButtonItem>
        </PanelSectionRow>
        {showCheck ? (
          <>
            <PanelSectionRow>
              <ToggleField
                label="Подробный журнал"
                description={
                  <div style={S.hint}>Записывать каждое соединение: какая программа, куда, через прокси или напрямую.</div>
                }
                checked={status?.verbose ?? false}
                disabled={busy}
                onChange={(on) => void apply(() => setVerbose(on))}
              />
            </PanelSectionRow>
            <PanelSectionRow>
              <ButtonItem layout="below" disabled={checking} onClick={() => void runCheck()}>
                {checking ? "Проверка… (до 30 секунд)" : "Проверить соединение"}
              </ButtonItem>
            </PanelSectionRow>
            {!diag && !checking ? (
              <Hint focusable={true}>
                Приложение висит? Подключитесь, включите «Подробный журнал», запустите приложение, дождитесь зависания и
                нажмите «Проверить соединение».
              </Hint>
            ) : null}
            {diag?.verdicts.map((line, i) => (
              <Hint key={`v${i}`} focusable={true}>
                {line}
              </Hint>
            ))}
            {diag?.proxy_leftover ? (
              <PanelSectionRow>
                <ButtonItem layout="below" onClick={() => void onResetProxy()}>
                  Сбросить системный прокси
                </ButtonItem>
              </PanelSectionRow>
            ) : null}
            {diag?.report ? <Hint focusable={true}>{`Полный отчёт: ${diag.report}`}</Hint> : null}
          </>
        ) : null}
      </PanelSection>
    </>
  );
}

export default definePlugin(() => {
  return {
    name: "Secure Connect",
    titleView: <div className={staticClasses.Title}>Secure Connect</div>,
    content: <Content />,
    icon: <FaShieldAlt />,
    onDismount() {},
  };
});
