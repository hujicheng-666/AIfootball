import type { AppState } from "../lib/bridge";
import { hasHost, navigate, runCommand } from "../lib/bridge";
import { DraftInput } from "../components/DraftInput";

/**
 * 采集 = 现场动作：接相机、看画面、录下来。
 * 上方取流预览占满主视觉，参数压到底部一条，避免画面被表单挤小。
 */
export function CapturePage({ state }: { state: AppState }) {
  const { run, form } = state;
  const setField = (field: string, value: unknown) =>
    runCommand("pipeline.setField", { field, value });

  return (
    <div className="scroll">
      <div className="page">
        <div className="head">
          <div>
            <div className="title">采集</div>
            <div className="sub">录制完成后自动加入分析队列</div>
          </div>
          <div className="head-actions">
            <button className="btn" onClick={() => navigate("analyze")}>
              查看分析队列
            </button>
          </div>
        </div>

        <section className="card grow">
          <div className="sect grow">
            <div className="sec-head">
              <span className="sec-title">实时取流</span>
              <span className="sec-note">
                {run.previewVisible ? "预览模式 · 不落盘" : "取流未启动"}
              </span>
            </div>

            {run.previewVisible ? (
              <div className="preview-grid">
                {(["left", "right"] as const).map((side) => {
                  const stamp = side === "left" ? run.previewLeftStamp : run.previewRightStamp;
                  const label = side === "left" ? "CAM-L" : "CAM-R";
                  return (
                    <div className="preview-box" key={side}>
                      {hasHost && stamp > 0 ? (
                        <img
                          alt={side === "left" ? "左相机画面" : "右相机画面"}
                          src={`https://preview.local/live_preview_${side}.jpg?v=${stamp}`}
                        />
                      ) : (
                        <span className="wait">等待视频帧</span>
                      )}
                      <span className="preview-tag">{label}</span>
                      {run.running && (
                        <span className="live-dot">
                          <i /> LIVE
                        </span>
                      )}
                    </div>
                  );
                })}
              </div>
            ) : (
              <div className="empty-box empty-stage">
                启动预览以确认双路取景，构图校验通过后开始录制
              </div>
            )}

            <div className="actions" style={{ marginTop: 10 }}>
              {!run.previewVisible && (
                <button
                  className="btn ok"
                  onClick={() => runCommand("pipeline.runOnline")}
                  disabled={run.running}
                >
                  启动取流预览
                </button>
              )}
              {run.previewing && (
                <button className="btn ok" onClick={() => runCommand("pipeline.startAnalysis")}>
                  开始录制
                </button>
              )}
              {run.previewVisible && !run.running && (
                <button className="btn" onClick={() => runCommand("pipeline.runOnline")}>
                  重新启动取流
                </button>
              )}
              {run.analyzing && (
                <button className="btn danger" onClick={() => runCommand("pipeline.stopAnalysis")}>
                  停止录制
                </button>
              )}
              {run.running && (
                <button
                  className="btn"
                  style={{ marginLeft: "auto" }}
                  onClick={() => runCommand("pipeline.cancel")}
                >
                  终止任务
                </button>
              )}
              {run.progressText && run.running && (
                <span className="notice" style={{ marginTop: 0 }}>
                  {run.progressText}
                </span>
              )}
            </div>
          </div>
        </section>

        <div className="cols fixed tall">
          <section className="card">
            <div className="sect grow spread">
              <div className="sec-head">
                <span className="sec-title">视频输入</span>
                <span className="sec-note">{form.usbMode ? "UVC" : "UDP 组播"}</span>
              </div>

              <div className="tabs" style={{ marginBottom: 6 }}>
                <button
                  className={form.usbMode ? "tab on" : "tab"}
                  onClick={() => setField("usbMode", true)}
                >
                  USB 相机
                </button>
                <button
                  className={form.networkMode ? "tab on" : "tab"}
                  onClick={() => setField("networkMode", true)}
                >
                  IP 相机
                </button>
              </div>

              {form.usbMode ? (
                <>
                  <div className="field">
                    <span className="k">左相机索引</span>
                    <DraftInput
                      className="input sm"
                      value={form.camLeft}
                      onCommit={(v) => setField("camLeft", v)}
                    />
                    <span className="k" style={{ flex: "0 0 auto", textAlign: "right" }}>
                      右相机索引
                    </span>
                    <DraftInput
                      className="input sm"
                      value={form.camRight}
                      onCommit={(v) => setField("camRight", v)}
                    />
                  </div>
                  <div className="actions">
                    <button
                      className="btn"
                      onClick={() => runCommand("pipeline.detectCameras")}
                      disabled={run.running}
                    >
                      枚举设备
                    </button>
                  </div>
                  {form.camerasText && <div className="notice">{form.camerasText}</div>}
                </>
              ) : (
                <>
                  <div className="field">
                    <span className="k">左路 URL</span>
                    <DraftInput
                      className="input"
                      value={form.urlLeft}
                      onCommit={(v) => setField("urlLeft", v)}
                    />
                  </div>
                  <div className="field">
                    <span className="k">右路 URL</span>
                    <DraftInput
                      className="input"
                      value={form.urlRight}
                      onCommit={(v) => setField("urlRight", v)}
                    />
                  </div>
                  <div className="actions">
                    <button
                      className="btn"
                      onClick={() => runCommand("pipeline.testNetwork")}
                      disabled={run.running || run.testingStream}
                    >
                      {run.testingStream ? "测试中…" : "连通性测试"}
                    </button>
                  </div>
                  {form.networkTestText && <div className="notice">{form.networkTestText}</div>}
                  <div className="notice">
                    默认端口 5000 / 5001，协议兼容 RTSP、MJPEG、HTTP；录制分辨率须与标定一致。
                  </div>
                </>
              )}
            </div>
          </section>

          <section className="card">
            <div className="sect grow spread">
              <div className="sec-head">
                <span className="sec-title">输出</span>
              </div>
              <div className="field">
                <span className="k">样本标识</span>
                <DraftInput
                  className="input"
                  value={form.sampleName}
                  onCommit={(v) => setField("sampleName", v)}
                />
              </div>
              <label className="check" style={{ marginTop: 4 }}>
                <input
                  type="checkbox"
                  checked={form.liveAnalysis}
                  onChange={(e) => setField("liveAnalysis", e.target.checked)}
                />
                录制时同步执行推理
              </label>
              <div className="hint" style={{ marginTop: 10 }}>
                采样帧率与分辨率沿用相机当前设置；录制产物直接进入分析队列。
              </div>
            </div>
          </section>
        </div>
      </div>
    </div>
  );
}
