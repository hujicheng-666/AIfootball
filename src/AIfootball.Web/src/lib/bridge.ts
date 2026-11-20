/** 前端与 WPF 宿主之间的唯一通道。 */

export type Sample = {
  name: string;
  videos: number;
  has3D: boolean;
  hasBallistic: boolean;
  hasCsv: boolean;
  shotSummary: string;
  verdict: string;
  updated: string;
};

export type KeeperAttrs = {
  height: number;
  diveSpeed: number;
  jumpDistance: number;
  jumpHeight: number;
  reach: number;
  tendGoalSpeed: number;
  goalKeeping: number;
  sidePreference: number;
  heightPreference: number;
};

export type Keeper = {
  name: string;
  displayName: string;
  description: string;
  attrs: KeeperAttrs;
  /** 低→高、左→右摊平的 15 格扑救概率；空数组表示这份 JSON 没有概率场。 */
  zones: number[];
  hasZones: boolean;
};

export type CalibrationState = {
  intrinsics: boolean;
  extrinsics: boolean;
  ready: boolean;
  running: boolean;
  progress: number;
  progressText: string;
  leftChess: string;
  rightChess: string;
  leftField: string;
  rightField: string;
  intrinsicsStatus: string;
  extrinsicsStatus: string;
  showWorkflow: boolean;
};

export type RunStep = {
  name: string;
  description: string;
  status: "pending" | "running" | "completed" | "failed" | "skipped";
};

export type RunState = {
  running: boolean;
  previewing: boolean;
  analyzing: boolean;
  testingStream: boolean;
  progress: number;
  progressText: string;
  previewVisible: boolean;
  previewLeftStamp: number;
  previewRightStamp: number;
  steps: RunStep[];
};

export type FormState = {
  samples: { name: string; selected: boolean }[];
  skipReconstruct: boolean;
  skipBallistic: boolean;
  selectedGoalkeeper: string;
  usbMode: boolean;
  networkMode: boolean;
  camLeft: string;
  camRight: string;
  camerasText: string;
  urlLeft: string;
  urlRight: string;
  networkTestText: string;
  sampleName: string;
  liveAnalysis: boolean;
};

export type AppState = {
  env: { ready: boolean; checking: boolean; statusText: string; settingUp: boolean; setupProgress: number };
  gpu: { name: string; cudaAvailable: boolean; dedicated: boolean; cudaVersion: string };
  calibration: CalibrationState;
  pipeline: { stage: number; statusText: string };
  run: RunState;
  form: FormState;
  samples: Sample[];
  goalkeepers: Keeper[];
  shooter: {
    hasData: boolean;
    countText: string;
    lines: string[];
    radarLabels: string[];
    radarValues: number[];
    emptyText: string;
  };
  statusBar: string;
  version: string;
};

type HostMessage =
  | { type: "state"; payload: AppState }
  | { type: "route"; page: string }
  | { type: "navigate"; page: string }
  | { type: "command"; name: string };

/**
 * WebView2 的宿主对象。注意消息必须挂在 chrome.webview 上，
 * 用 window.addEventListener("message") 是收不到的——这是最容易踩的一个坑。
 */
type WebView2Webview = {
  postMessage: (msg: unknown) => void;
  addEventListener: (type: "message", handler: (e: { data: unknown }) => void) => void;
  removeEventListener: (type: "message", handler: (e: { data: unknown }) => void) => void;
};

type WebView2Host = { chrome?: { webview?: WebView2Webview } };

const webview = (window as unknown as WebView2Host).chrome?.webview;

/** 在浏览器里直接打开（npm run dev）时没有宿主，用假数据把界面跑起来。 */
export const hasHost = Boolean(webview?.postMessage);

export function send(message: object) {
  webview?.postMessage(message);
}

export function notifyReady() {
  send({ type: "ready" });
}

export function navigate(page: string) {
  send({ type: "navigate", page });
}

/** 无参数命令（refresh / newSample / pipeline.runOffline…）。 */
export function runCommand(name: string, payload?: unknown) {
  send(payload === undefined ? { type: "command", name } : { type: "command", name, payload });
}

export function onHostMessage(handler: (msg: HostMessage) => void): () => void {
  if (!webview?.addEventListener) return () => {};

  const listener = (e: { data: unknown }) => {
    const data = e.data as HostMessage | undefined;
    if (data && typeof data === "object" && "type" in data) handler(data);
  };

  webview.addEventListener("message", listener);
  return () => webview.removeEventListener("message", listener);
}
