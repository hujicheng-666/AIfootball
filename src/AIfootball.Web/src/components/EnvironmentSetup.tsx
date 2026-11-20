import type { AppState } from "../lib/bridge";
import { runCommand } from "../lib/bridge";

export function EnvironmentSetup({ env }: { env: AppState["env"] }) {
  if (env.ready || env.checking) return null;

  return (
    <button
      className="btn"
      disabled={env.settingUp}
      onClick={() => runCommand("environment.setup")}
    >
      {env.settingUp ? `安装环境中 · ${env.setupProgress}%` : "一键安装环境"}
    </button>
  );
}
