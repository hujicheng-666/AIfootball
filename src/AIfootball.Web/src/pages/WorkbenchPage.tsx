import type { AppState, RunStep } from "../lib/bridge";
import { navigate, runCommand } from "../lib/bridge";
import { EnvironmentSetup } from "../components/EnvironmentSetup";

const stepMark: Record<RunStep["status"], string> = {
  pending: "○",
  running: "◉",
  completed: "✓",
  failed: "✗",
  skipped: "→",
};

const stepText: Record<RunStep["status"], string> = {
  pending: "待执行",
  running: "执行中",
  completed: "已完成",
  failed: "失败",
  skipped: "已跳过",
};

/**
 * 工作台回答三个问题：还差什么、算过什么、现在能不能算。
 * 四块等分一屏：待处理 / 前置条件 / 已完成 / 最近任务。
 */
export function WorkbenchPage({ state }: { state: AppState }) {
  const { samples, goalkeepers, calibration, env, gpu, run } = state;

  const pending = samples.filter((s) => !s.hasBallistic);
  const done = samples.filter((s) => s.hasBallistic);

  const statusLine = [
    calibration.ready ? "双目立体标定：就绪" : "双目立体标定：待执行",
    `待处理样本：${pending.length}`,
    `门将模型：${goalkeepers.length}`,
  ].join(" · ");

  return (
    <div className="scroll">
      <div className="page">
        <div className="head">
          <div>
            <div className="title">工作台</div>
            <div className="sub">{statusLine}</div>
          </div>
          <div className="head-actions">
            {pending.length > 0 ? (
              <button className="btn primary" onClick={() => navigate("analyze")}>
                进入分析
              </button>
            ) : (
              <button className="btn" onClick={() => navigate("capture")}>
                采集新样本
              </button>
            )}
          </div>
        </div>

        <div className="grid2">
          <section className="card">
            <div className="sect grow">
              <div className="sec-head">
                <span className="sec-title">待处理样本</span>
                <span className="sec-right">
                  共 <b className="tabular">{pending.length}</b> 个
                </span>
              </div>

              {pending.length === 0 ? (
                <div className="empty-box">
                  全部样本均已生成弹道解。新增素材请通过「采集」录入，或放入 samples/ 目录后重新扫描。
                </div>
              ) : (
                <div className={pending.length >= 2 ? "tbl-wrap" : "tbl-wrap nofill"}>
                  <table className={pending.length >= 2 ? "tbl fill" : "tbl"}>
                    <thead>
                      <tr>
                        <th style={{ width: "34%" }}>样本</th>
                        <th style={{ width: "16%" }}>通道</th>
                        <th style={{ width: "26%" }}>缺失产物</th>
                        <th style={{ width: "24%" }}>最后更新</th>
                      </tr>
                    </thead>
                    <tbody>
                      {pending.map((s) => (
                        <tr
                          key={s.name}
                          style={{ cursor: "pointer" }}
                          onClick={() => navigate("analyze")}
                        >
                          <td>
                            <span className="c-name">
                              <span className="n">{s.name}</span>
                            </span>
                          </td>
                          <td className="tabular">{s.videos} 路</td>
                          <td>
                            <span className="judge none">
                              {[!s.has3D ? "三维重建" : "", !s.hasBallistic ? "弹道解算" : ""]
                                .filter(Boolean)
                                .join(" · ") || "未产出"}
                            </span>
                          </td>
                          <td className="c-time">{s.updated}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}

              <div className="flow">
                <span className="flow-step">三维重建</span>
                <span className="flow-arrow">→</span>
                <span className="flow-step">弹道解算</span>
                <span className="flow-arrow">→</span>
                <span className="flow-step">落点判定</span>
              </div>

              <div className="foot-actions">
                <div className="foot-row">
                  <span className="hint" style={{ margin: 0 }}>
                    样本目录 samples/
                  </span>
                  <button className="btn" onClick={() => runCommand("refresh")}>
                    重新扫描
                  </button>
                </div>
              </div>
            </div>
          </section>

          <section className="card">
            <div className="sect grow">
              <div className="sec-head">
                <span className="sec-title">运行前置条件</span>
              </div>

              <div className="calib-row">
                <span className="calib-k">计算环境</span>
                <span className="calib-v">{env.statusText || "未检测"}</span>
                <span className={env.ready ? "calib-ok" : "calib-no"}>
                  {env.ready ? "通过" : env.checking ? "检查中" : env.settingUp ? "安装中" : "未就绪"}
                </span>
              </div>
              <div className="calib-row">
                <span className="calib-k">双目标定</span>
                <span className="calib-v">
                  {calibration.ready ? "内参与外参齐备" : "内参或外参缺失"}
                </span>
                <span className={calibration.ready ? "calib-ok" : "calib-no"}>
                  {calibration.ready ? "已就绪" : "待标定"}
                </span>
              </div>
              <div className="calib-row">
                <span className="calib-k">推理设备</span>
                <span className="calib-v" title={gpu.name || undefined}>
                  {gpu.name || "未检测到可用 GPU"}
                </span>
                <span className={gpu.cudaAvailable ? "calib-ok" : "calib-no"}>
                  {gpu.cudaAvailable ? "CUDA" : "CPU"}
                </span>
              </div>

              <div className="foot-actions">
                <div className="actions" style={{ marginTop: 0 }}>
                  <EnvironmentSetup env={env} />
                  {!calibration.ready && (
                    <button className="btn" onClick={() => navigate("settings")}>
                      执行标定
                    </button>
                  )}
                  <button className="btn" onClick={() => runCommand("refresh")}>
                    重新扫描样本目录
                  </button>
                </div>
                <div className="hint">样本目录 samples/；环境与设备详情见「设置 · 环境诊断」。</div>
              </div>
            </div>
          </section>

          <section className="card">
            <div className="sect grow">
              <div className="sec-head">
                <span className="sec-title">已完成样本</span>
                <span className="sec-note">选择行以进入回放</span>
              </div>

              {done.length === 0 ? (
                <div className="empty-box">暂无可回放样本</div>
              ) : (
                <div className="tbl-wrap">
                  <table className={done.length >= 2 ? "tbl fill" : "tbl"}>
                    <thead>
                      <tr>
                        <th style={{ width: "38%" }}>样本</th>
                        <th style={{ width: "34%" }}>落点判定</th>
                        <th style={{ width: "28%" }}>最后更新</th>
                      </tr>
                    </thead>
                    <tbody>
                      {done.map((s) => (
                        <tr
                          key={s.name}
                          style={{ cursor: "pointer" }}
                          onClick={() => navigate("review")}
                        >
                          <td>
                            <span className="c-name">
                              <span className="n">{s.name}</span>
                            </span>
                          </td>
                          <td>
                            <span className={s.verdict === "射正" ? "judge ok" : "judge miss"}>
                              {s.shotSummary || "已解算"}
                            </span>
                          </td>
                          <td className="c-time">{s.updated}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}

              <div className="foot-actions">
                <div className="foot-row">
                  <span className="hint" style={{ margin: 0 }}>
                    {done.length} 个样本可回放
                  </span>
                  <button className="btn" onClick={() => navigate("review")} disabled={done.length === 0}>
                    打开回放
                  </button>
                </div>
              </div>
            </div>
          </section>

          <section className="card">
            <div className="sect grow">
              <div className="sec-head">
                <span className="sec-title">最近任务</span>
                <span className="sec-right">
                  <b className="tabular">{run.progress}</b>%
                </span>
              </div>

              {run.steps.length === 0 ? (
                <div className="empty-box">本次会话尚未启动分析任务</div>
              ) : (
                <div className="run-steps">
                  {run.steps.map((step, i) => (
                    <div className={`run-step ${step.status}`} key={`${step.name}-${i}`}>
                      <span className="rs-dot">{stepMark[step.status] ?? "○"}</span>
                      <span className="rs-name">
                        {step.name}
                        {step.description && <span className="rs-desc">{step.description}</span>}
                      </span>
                      <span className="rs-status">{stepText[step.status] ?? step.status}</span>
                    </div>
                  ))}
                </div>
              )}

              <div className="foot-actions">
                <div className="progress-text" title={run.progressText}>
                  {run.progressText || "待启动"}
                </div>
                <div
                  className={run.running ? "progress" : "progress idle"}
                  style={{ marginTop: 8 }}
                >
                  <i style={{ width: `${Math.max(run.running ? 3 : 0, run.progress)}%` }} />
                </div>
              </div>
            </div>
          </section>
        </div>
      </div>
    </div>
  );
}
