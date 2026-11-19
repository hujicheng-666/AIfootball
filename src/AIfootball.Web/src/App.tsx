import { useEffect, useState } from "react";
import { hasHost, onHostMessage, notifyReady, type AppState } from "./lib/bridge";
import { mockState } from "./lib/mock";
import { WorkbenchPage } from "./pages/WorkbenchPage";
import { CapturePage } from "./pages/CapturePage";
import { AnalyzePage } from "./pages/AnalyzePage";
import { SettingsPage } from "./pages/SettingsPage";

/**
 * 路由由 WPF 宿主决定：侧栏在 WPF 里，点击后宿主推 {"type":"route"} 过来。
 * 前端不自己切页，避免两边状态打架。
 */
export default function App() {
  const [state, setState] = useState<AppState | null>(hasHost ? null : mockState);
  // WebView2 里路由完全由宿主推；浏览器预览时用 hash 指定要看哪一页。
  const [route, setRoute] = useState(
    () => (hasHost ? "" : window.location.hash.replace("#", "").split("/")[0]) || "workbench",
  );

  useEffect(() => {
    const off = onHostMessage((msg) => {
      if (msg.type === "state") setState(msg.payload);
      else if (msg.type === "route") setRoute(msg.page);
    });
    // 告诉宿主"页面挂载完了，可以推状态了"
    notifyReady();
    return off;
  }, []);

  if (!state) {
    return (
      <div className="boot">
        <span>正在载入工作区状态</span>
      </div>
    );
  }

  if (route === "capture") return <CapturePage state={state} />;
  if (route === "analyze") return <AnalyzePage state={state} />;
  if (route === "settings") return <SettingsPage state={state} />;
  // review 由 WPF 承载（Unity 贴窗），Web 侧不会收到；收到也回工作台，不留白屏。
  return <WorkbenchPage state={state} />;
}
