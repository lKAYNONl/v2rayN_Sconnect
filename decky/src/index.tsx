import {
  ButtonItem,
  DropdownItem,
  Field,
  PanelSection,
  PanelSectionRow,
  TextField,
  ToggleField,
  staticClasses,
} from "@decky/ui";
import { callable, definePlugin, toaster } from "@decky/api";
import { useEffect, useState } from "react";
import { FaShieldAlt } from "react-icons/fa";

interface Profile {
  id: string;
  name: string;
  type: string;
  address: string;
  port: number;
  group: string;
}

interface ProfilesAnswer {
  ok: boolean;
  error?: string;
  active?: string;
  selected?: string;
  profiles?: Profile[];
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

function profileLabel(p: Profile): string {
  const name = p.name || `${p.address}:${p.port}`;
  return p.group ? `${name} · ${p.group}` : name;
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
  const [profiles, setProfiles] = useState<Profile[]>([]);
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

  const loadProfiles = async () => {
    setLoading(true);
    try {
      const answer = await getProfiles();
      if (answer.ok) {
        const list = answer.profiles ?? [];
        setProfiles(list);
        setListError(list.length === 0 ? "Профилей нет — добавьте их в приложении (Desktop Mode)." : "");
        setSelected((current) => current || answer.selected || answer.active || (list.length > 0 ? list[0].id : ""));
      } else {
        setListError(answer.error ?? "Не удалось получить профили.");
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
    // Switching profiles while connected reconnects to the new one.
    if (running && id !== status?.profile_id) {
      void apply(() => connect(id));
    }
  };

  let description = "Отключено";
  if (busy) {
    description = running ? "Применение…" : "Подключение…";
  } else if (running) {
    description = `${split?.ok ? MODE_STATUS[mode] : "Подключено"} · ${status?.profile_name ?? ""}`;
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
      <PanelSection title="Secure Connect">
        <PanelSectionRow>
          <ToggleField
            label={running ? "Подключено" : "Подключить"}
            description={description}
            checked={running}
            disabled={busy || (!running && !selected)}
            onChange={onToggle}
          />
        </PanelSectionRow>
        {status?.error ? (
          <PanelSectionRow>
            <Field label="Ошибка" description={status.error} />
          </PanelSectionRow>
        ) : null}
        <PanelSectionRow>
          <DropdownItem
            label="Профиль"
            rgOptions={profiles.map((p) => ({ data: p.id, label: profileLabel(p) }))}
            selectedOption={selected}
            strDefaultLabel={loading ? "Загрузка…" : "Выберите профиль"}
            disabled={busy || profiles.length === 0}
            onChange={(option) => onSelect(String(option.data))}
          />
        </PanelSectionRow>
        {listError ? (
          <PanelSectionRow>
            <Field description={listError} />
          </PanelSectionRow>
        ) : null}
        <PanelSectionRow>
          <ButtonItem layout="below" disabled={loading} onClick={() => void loadProfiles()}>
            {loading ? "Загрузка…" : "Обновить список профилей"}
          </ButtonItem>
        </PanelSectionRow>
      </PanelSection>

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
                description={MODE_HINT[mode]}
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
                  split?.routing ?? true
                    ? "Вкл: адреса из правил набора маршрутизации (например, локальные сайты) идут напрямую."
                    : "Выкл: весь трафик туннеля — через прокси, напрямую только локальная сеть."
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
                {emptyHint ? (
                  <PanelSectionRow>
                    <Field description={emptyHint} />
                  </PanelSectionRow>
                ) : null}
                {apps.length > 0 && subnets > 0 ? (
                  <PanelSectionRow>
                    <Field description={`Ещё подсетей из приложения: ${subnets}.`} />
                  </PanelSectionRow>
                ) : null}

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
                    {programs === null ? (
                      <PanelSectionRow>
                        <Field description="Загрузка списка процессов…" />
                      </PanelSectionRow>
                    ) : null}
                    {programsError ? (
                      <PanelSectionRow>
                        <Field label="Ошибка" description={programsError} />
                      </PanelSectionRow>
                    ) : null}
                    {programs !== null && found.length === 0 && !programsError ? (
                      <PanelSectionRow>
                        <Field description={q ? "Ничего не найдено." : "Нет подходящих процессов."} />
                      </PanelSectionRow>
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
                      <PanelSectionRow>
                        <Field description={`Показаны первые ${shown.length} из ${found.length} — уточните поиск.`} />
                      </PanelSectionRow>
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
                <PanelSectionRow>
                  <Field
                    description={
                      "Игры на Proton добавляются вместе с версией Proton: в системе у них виден не .exe " +
                      "игры, а загрузчик Wine. «● в сети» — процесс сейчас использует сеть."
                    }
                  />
                </PanelSectionRow>
              </>
            ) : null}
          </>
        )}
        <PanelSectionRow>
          <Field
            description={
              "Ядро — sing-box в режиме TUN. Режим и список общие с приложением «Secure Connect (v2rayN)» " +
              "в режиме рабочего стола (кроме «Весь Deck» — он только для игрового режима)."
            }
          />
        </PanelSectionRow>
      </PanelSection>

      <PanelSection title="Проверка">
        <PanelSectionRow>
          <ToggleField
            label="Подробный журнал"
            description="Записывать каждое соединение: какая программа, куда, через прокси или напрямую."
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
          <PanelSectionRow>
            <Field
              description={
                "Приложение висит? Подключитесь, включите «Подробный журнал», запустите приложение, " +
                "дождитесь зависания и нажмите «Проверить соединение»."
              }
            />
          </PanelSectionRow>
        ) : null}
        {diag?.verdicts.map((line, i) => (
          <PanelSectionRow key={`v${i}`}>
            <Field description={line} />
          </PanelSectionRow>
        ))}
        {diag?.proxy_leftover ? (
          <PanelSectionRow>
            <ButtonItem layout="below" onClick={() => void onResetProxy()}>
              Сбросить системный прокси
            </ButtonItem>
          </PanelSectionRow>
        ) : null}
        {diag?.report ? (
          <PanelSectionRow>
            <Field description={`Полный отчёт: ${diag.report}`} />
          </PanelSectionRow>
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
