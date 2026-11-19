# WPF 壳 + WebView2（React）架构说明

内容区界面已按「工作流」重排：工作台 / 采集 / 分析 / 设置四页由 WebView2 渲染，回放页留在 WPF。

## 分工

| 层 | 负责 | 位置 |
|---|---|---|
| WPF | 窗口、自绘标题栏、侧栏导航、状态栏、Unity 贴窗、本地能力 | `src/AIfootball.App/` |
| WebView2 | 内容区界面（React） | `src/AIfootball.Web/` |
| 桥接 | 状态单向推送 + 命令回传 | `Views/Web/WebHost.xaml.cs`、`src/lib/bridge.ts` |

前端**不碰文件系统**。所有数据由 C# 侧 `DashboardStateBuilder` 从 `MainViewModel` + `PipelineViewModel`
序列化后推给前端；前端只发「导航」和「命令」两类意图，具体动作仍由 `MainWindow.OnWebCommand` 决定。

## 导航结构

侧栏分两组，低频动作不与主流程平级：

```
工作流          配置
  工作台         设置 ── 相机标定 / 门将库 / 环境与设备
  采集
  分析
  回放（WPF）
```

旧 key 仍可用（`dashboard→workbench`、`pipeline→analyze`、`calibration→settings`、`results→review`），
见 `MainWindow.NavigateTo` 的映射表，页面内部的旧跳转不必一次性改完。

## 构建

改动前端后必须重新构建，否则跑的还是旧的 `web/`：

```bash
cd src/AIfootball.Web
npm install          # 首次
npm run build        # 产出 dist/
cd ../..
dotnet build src/AIfootball.App/AIfootball.App.csproj -c Debug
```

`dotnet build` 结束时由 `CopyWebUi` 目标把 `dist/` 拷到输出目录的 `web/`。
没有用 `<Content Include>`，因为 vite 每次构建都会换文件名，会和 dotnet 抢文件导致 MSB3030。

调界面时可以开热更新，不用每次 build：

```bash
cd src/AIfootball.Web && npm run dev      # http://localhost:5173
set AIFOOTBALL_WEB_DEV=http://localhost:5173
```

浏览器里直接看某一页：`#workbench` / `#capture` / `#analyze` / `#settings`、`#settings/keepers`。
（WebView2 里 hash 为空，路由完全由宿主推送。）

## 桥接协议

C# → JS：

```jsonc
{ "type": "state", "payload": { /* DashboardStateDto，camelCase */ } }
{ "type": "route", "page": "analyze" }        // 宿主是导航的唯一权威
```

JS → C#：

```jsonc
{ "type": "ready" }                            // 页面挂载完成，请求首帧状态
{ "type": "navigate", "page": "capture" }
{ "type": "command", "name": "refresh" }
{ "type": "command", "name": "pipeline.setField", "payload": { "field": "camLeft", "value": "1" } }
```

加一个字段的流程：`DashboardStateDto` 加属性 → `DashboardStateBuilder.Build` 填值 →
`src/lib/bridge.ts` 的 `AppState` 加类型 → 组件里用。两个 ViewModel 的属性变化都会自动触发推送
（120ms 合并），无需手动调用。

命令清单见 `MainWindow.OnWebCommand`：`refresh`、`newSample`/`capture`、`pipeline.*`（runOffline /
runOnline / detectCameras / testNetwork / cancel / startAnalysis / stopAnalysis / pickSamples / setField）、
`calibration.*`（browse / runIntrinsics / runExtrinsics / recalibrate）。

## 四个必须记住的坑

1. **airspace 冲突。** WebView2 是独立 HWND，不能与 `HwndHost`（Unity 回放）、`MediaElement` 重叠。
   切到回放页时必须把 WebView2 `Collapsed`，见 `MainWindow.NavItem_Checked`。
   回放页若将来要做数据侧栏，只能左右分栏，不能让 Unity 区域压在 WebView2 上。

2. **消息监听必须挂在 `chrome.webview` 上。** 用 `window.addEventListener("message")` 收不到任何东西，
   页面会一直停在"正在读取工作区"。

3. **WPF 色板与 CSS token 必须同源。** 改一个要改另一个，否则会出现两种蓝：
   - CSS：`src/AIfootball.Web/src/styles/app.css` 的 `:root`
   - XAML：`Themes/Colors.xaml`

4. **在线预览图走虚拟主机，不要转 base64。** `preview.local` 映射到 `output/live_preview/`，
   前端 `<img src="https://preview.local/live_preview_left.jpg?v=<stamp>">`，
   用 `run.previewLeftStamp` 做缓存击穿。逐帧转 base64 推 JSON 会吃掉主线程。

## 迁移状态

| 页面 | 渲染方 | 状态 | 备注 |
|---|---|---|---|
| 工作台 | WebView2 | ✅ | 待分析 / 最近结果 / 能不能开始 |
| 采集 | WebView2 | ✅ | 在线预览、相机源、采样参数 |
| 分析 | WebView2 | ✅ | 队列勾选、门将方案、跳过项、进度步骤 |
| 设置 | WebView2 | ✅ | 相机标定、门将库（雷达 + 15 格概率）、环境与设备 |
| 回放 | WPF | ➖ 保留 | Unity 靠 HwndHost 贴窗，训练器用 MediaElement，无法进 WebView2 |

回放页工具栏已按用途分组（播放 / 速度 / 门将 / 数据），避免九个按钮并排。

## 当前已知差异

- 回放页仍是 WPF 渲染，卡片用了 `DropShadowEffect`（软件渲染），阴影比 React 侧重。
- 侧栏导航没有图标（设计稿也没有），后续若要加，走 XAML Path 而不是字体图标。
