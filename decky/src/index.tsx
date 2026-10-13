import {
  ButtonItem,
  DropdownItem,
  Field,
  PanelSection,
  PanelSectionRow,
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
}

const getProfiles = callable<[], ProfilesAnswer>("get_profiles");
const getStatus = callable<[], Status>("get_status");
const connect = callable<[string], Status>("connect");
const disconnect = callable<[], Status>("disconnect");

function profileLabel(p: Profile): string {
  const name = p.name || `${p.address}:${p.port}`;
  return p.group ? `${name} · ${p.group}` : name;
}

function Content() {
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [selected, setSelected] = useState<string>("");
  const [listError, setListError] = useState<string>("");
  const [loading, setLoading] = useState<boolean>(false);
  const [status, setStatus] = useState<Status | null>(null);
  const [working, setWorking] = useState<boolean>(false);

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

  const refreshStatus = async () => {
    try {
      setStatus(await getStatus());
    } catch {
      // the backend is restarting; the next tick will catch up
    }
  };

  useEffect(() => {
    void loadProfiles();
    void refreshStatus();
    const timer = window.setInterval(() => void refreshStatus(), 3000);
    return () => window.clearInterval(timer);
  }, []);

  const apply = async (action: () => Promise<Status>) => {
    setWorking(true);
    try {
      const next = await action();
      setStatus(next);
      // A profile that failed to start leaves the previous session (if any) up.
      if (next.running && next.profile_id) {
        setSelected(next.profile_id);
      }
      if (next.error) {
        toaster.toast({ title: "Secure Connect", body: next.error });
      }
    } catch (e) {
      toaster.toast({ title: "Secure Connect", body: String(e) });
    } finally {
      setWorking(false);
    }
  };

  const running = status?.running ?? false;
  const busy = working || (status?.busy ?? false);

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
    description = running ? "Переключение…" : "Подключение…";
  } else if (running) {
    description = `Весь трафик через туннель · ${status?.profile_name ?? ""}`;
  }

  return (
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
      <PanelSectionRow>
        <Field
          description={
            "Ядро — sing-box в режиме TUN. Профили, подписки, маршруты и раздельное " +
            "туннелирование настраиваются в приложении «Secure Connect (v2rayN)» в режиме рабочего стола."
          }
        />
      </PanelSectionRow>
    </PanelSection>
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
