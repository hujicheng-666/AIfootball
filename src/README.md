# AIfootball v2.0 — AI 足球轨迹分析平台

> 双摄像头 3D 足球轨迹重建 · 弹道拟合 · Unity 可视化
> 专业化 WPF 桌面应用，支持 Windows Store 分发

---

## 🏗 项目架构

```
src/
├── AIfootball.sln                  # Visual Studio 解决方案
├── AIfootball.Engine/              # Python 推理引擎
│   ├── aifootball/
│   │   ├── __main__.py             # CLI 入口
│   │   ├── config.py               # 全局配置
│   │   ├── calibration/            # 相机标定
│   │   │   ├── intrinsics.py       # 内参标定（棋盘格）
│   │   │   └── extrinsics.py       # 外参标定（立体视觉）
│   │   ├── capture/                # 视频采集
│   │   │   └── dual_camera.py      # 双摄同步录制
│   │   ├── detection/              # 目标检测
│   │   │   └── ball_detector.py    # YOLO 足球检测
│   │   ├── reconstruction/         # 3D 重建
│   │   │   └── triangulator.py     # 双目三角测量
│   │   ├── trajectory/             # 轨迹处理
│   │   │   ├── ballistic_fit.py    # 弹道拟合
│   │   │   └── export.py           # Unity CSV 导出
│   │   └── pipeline/               # 流水线编排
│   │       ├── offline.py          # 离线批处理
│   │       └── online.py           # 在线实时处理
│   ├── requirements.txt
│   └── pyproject.toml
│
├── AIfootball.App/                 # WPF 桌面应用
│   ├── App.xaml/cs                 # 应用入口 + DI 容器
│   ├── MainWindow.xaml/cs          # 主窗口（导航+状态栏）
│   ├── Views/
│   │   └── Pages/
│   │       ├── DashboardPage       # 仪表盘（环境/GPU/样本概览）
│   │       ├── PipelinePage        # 流水线处理（离线+在线）
│   │       ├── CalibrationPage     # 相机标定操作
│   │       └── ResultsPage         # 结果查看与导出
│   ├── ViewModels/                 # MVVM ViewModels
│   │   ├── ViewModelBase.cs        # 基类 + RelayCommand
│   │   ├── MainViewModel.cs        # 仪表盘状态
│   │   └── PipelineViewModel.cs    # 流水线控制
│   ├── Models/                     # 数据模型
│   │   └── Models.cs
│   ├── Services/                   # 业务服务
│   │   ├── Interfaces/IServices.cs # 服务接口
│   │   ├── PythonEngineService.cs  # Python 进程管理
│   │   ├── EnvironmentService.cs   # 环境自动安装
│   │   ├── GpuDetectionService.cs  # GPU 检测
│   │   └── PipelineService.cs      # 流水线编排
│   ├── Themes/
│   │   ├── Colors.xaml             # 品牌色板
│   │   └── Styles.xaml             # 全局样式
│   └── Converters/
│       └── ValueConverters.cs      # XAML 绑定转换器
│
└── AIfootball.Package/             # MSIX 打包项目
    ├── AIfootball.Package.csproj
    └── Package.appxmanifest
```

## 🚀 快速开始

### 开发环境

- **.NET 8.0 SDK** — [下载](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Visual Studio 2022** (推荐) 或 VS Code
- **Windows 10/11** (≥19041)

### 运行开发版

```bash
# 1. 还原依赖
cd src
dotnet restore

# 2. 运行应用
dotnet run --project AIfootball.App

# 3. 首次运行会自动提示安装 Python 环境
```

### 发布构建

```bash
# 完整构建（MSIX + 绿色版）
scripts\build_release.bat

# 仅构建 WPF 应用
dotnet publish src/AIfootball.App -c Release -r win-x64 -o dist/app
```

## 📦 分发方式

### 方式一：MSIX 安装包（推荐）

```
dist/msix/
├── AIfootball_2.0.0.0_x64.msixbundle    # 安装包
└── AIfootball_2.0.0.0.appinstaller      # 自动更新清单
```

- 双击 `.msixbundle` 安装
- 支持自动更新
- 干净卸载，不留残留

### 方式二：绿色便携版

```
dist/AIfootball-Portable/
├── AIfootball.exe            # 主程序
├── python_engine/            # Python 推理引擎
├── calib/                    # 标定文件
├── models/                   # YOLO 模型
└── data/                     # 门将 + 样本
```

- 免安装，解压即用
- 适合 U 盘携带

## 🔄 首次运行流程

```
启动 AIfootball.exe
    │
    ▼
检测 Python 环境
    │
    ├── 已就绪 ──▶ 进入仪表盘
    │
    └── 未就绪
          │
          ▼
    一键安装环境
          │
          ├── ① 检测 GPU (NVIDIA CUDA / CPU)
          ├── ② 下载 Python 3.10 嵌入式版 (~8MB)
          ├── ③ 安装 pip
          ├── ④ 安装 PyTorch (CUDA ~2GB / CPU ~200MB)
          ├── ⑤ 安装 ultralytics (YOLO)
          └── ⑥ 验证完成 ──▶ 进入仪表盘
```

## 🧩 核心功能

| 模块 | 功能 | 状态 |
|------|------|------|
| 📷 相机标定 | 双目内参+外参标定 | ✅ |
| 🎥 在线录制 | 双摄同步采集+实时处理 | ✅ |
| 📁 离线处理 | 批量3D重建+弹道拟合 | ✅ |
| 🎯 弹道拟合 | 物理建模抛物线拟合 | ✅ |
| 🎮 Unity导出 | CSV → Unity 可视化 | ✅ |
| 🧠 GPU推理 | CUDA/CPU 自适应 | ✅ |
| 🥅 门将方案 | 多种扑救策略 | ✅ |

## 🔧 技术栈

| 层 | 技术 | 用途 |
|----|------|------|
| UI | WPF + Fluent Design | 桌面界面 |
| 架构 | MVVM + DI | 依赖注入 |
| 推理 | PyTorch + YOLOv11 | 足球检测 |
| 视觉 | OpenCV + SciPy | 标定+重建 |
| 通信 | Process stdin/stdout | C# ↔ Python |
| 打包 | MSIX | Windows Store |

## 📝 许可

内部项目 — AIfootball Team
