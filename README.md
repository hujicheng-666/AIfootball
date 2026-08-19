# AIfootball

面向点球训练的 Windows 桌面分析应用：使用左右双目视频重建足球三维轨迹、拟合弹道，并在内嵌 Unity 查看器中回放门将扑救。

## 启动

要求：Windows 10/11、.NET 8 SDK。首次进入应用后，可在“工作概览”中完成 Python 推理环境配置。

```powershell
dotnet run --project src\AIfootball.App\AIfootball.App.csproj
```

构建检查：

```powershell
dotnet build src\AIfootball.App\AIfootball.App.csproj
```

## 工作流

1. **相机标定**
   - 缺少完整内参或外参时，直接进入标定流程。
   - 已有完整标定结果时，页面只显示当前状态；点击“重新标定”才展开标定操作。

2. **准备样本**
   - 每个样本必须使用明确的左右相机目录，不再根据文件名猜测相机：

   ```text
   samples/
   └── sample_name/
       ├── left/
       │   └── recording.mp4
       └── right/
           └── recording.mp4
   ```

   - `left/` 与 `right/` 中各应有一个视频文件。应用会自动扫描样本目录变化。

3. **处理与交付**
   - 在“处理队列”中选择样本和门将，执行三维重建、弹道拟合与 Unity CSV 导出。
   - 在“输出与交付”中可导入轨迹、重新播放、切换视角、调整速度和切换门将。
   - 每次离线处理会将所选门将与轨迹交付结果绑定；再次打开该轨迹时，WPF 与 Unity 会恢复同一门将。

## 输出

```text
output/trajectory_3d/<sample>/        # 三维轨迹结果
output/trajectory_ballistic/<sample>/ # 弹道拟合结果
data/<sample>_trajectory.csv          # Unity 回放轨迹
data/<sample>_trajectory.meta.json    # 该轨迹绑定的门将（如有）
data/goalkeepers/                     # 门将配置
```

`output/`、运行时轨迹 CSV 和 Unity 播放器构建产物属于本地生成结果，默认不应作为源码提交。

## 项目结构

```text
src/AIfootball.App/       # WPF 应用
src/AIfootball.Engine/    # Python 引擎入口
runtime/project/          # 运行时 Python 脚本
Myproject/                # Unity 项目
samples/                  # 左右相机训练样本
```
