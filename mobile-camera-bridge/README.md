# AIfootball Camera Bridge（Android）

这是替代 IP Webcam 的手机端采集器。它直接使用 Android **Camera2** 列出手机实际支持的 JPEG 分辨率和 AE 帧率范围；应用本身不把画面锁在某个固定档位。

它输出标准 MJPEG/HTTP，所以 AIfootball 现有“手机 / 网络相机”输入无需改协议：

```text
http://<手机局域网-IP>:8080/video
```

每台手机安装一次，两台手机分别作为 left/right 路。手机与电脑须在同一局域网；开始推流后，把手机屏幕显示的地址填入桌面端相应输入框，再点“测试左路连接”。状态接口为 `http://<IP>:8080/status`，会返回当前分辨率和帧率范围。

## 构建

使用 Android Studio 打开本目录，等待 Gradle 同步后直接 Run 到 Android 8.0（API 26）及以上真机，或用 Android Studio 的 **Build > Build APK(s)** 生成安装包。

首次启动需要授予相机权限。分辨率与目标帧率是手机相机的真实可选组合；如果某个高分辨率 + 高帧率组合被设备拒绝，请选择该手机列表中更低的一档。MJPEG 的网络负载较高，双路 1080p/60 更建议使用 5 GHz/6 GHz Wi-Fi 或 Wi-Fi 6 路由器。

## 与标定的关系

采集分辨率一旦用于内参/外参标定，正式录制应保持同一分辨率与相机方向；改分辨率后需重新标定对应相机。
