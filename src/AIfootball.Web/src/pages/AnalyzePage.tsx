import type { AppState, RunStep } from "../lib/bridge";
import { runCommand } from "../lib/bridge";
import { Radar } from "../components/Radar";
import { EnvironmentSetup } from "../components/EnvironmentSetup";

const stepIcon: Record<RunStep["status"], string> = {
  pending: "○",
  running: "◉",
  completed: "✓",
  failed: "✗",
  skipped: "→",
};

const stepLabel: Record<RunStep["status"], string> = {
  pending: "待执行",
  running: "执行中",
  completed: "已完成",
  failed: "失败",
  skipped: "已跳过",
};

/**
 * 分析 = 回到电脑后跑的批任务。
 * 单页内完成「样本选择 → 参数配置 → 执行监控」。
 */
export function AnalyzePage({ state }: { state: AppState }) {
  const { env, run, form, samples, goalkeepers } = state;
  const selected = form.samples.filter((s) => s.selected).map((s) => s.name);
  const selectedSet = new Set(selected);

  const toggleSample = (name: string) => {
    const next = new Set(selected);
    if (next.has(name)) next.delete(name);
    else next.add(name);
    runCommand("pipeline.pickSamples", { names: [...next] });
  };

  const setField = (field: string, value: unknown) =>
    runCommand("pipeline.setField", { field, value });

  const info = (name: string) => samples.find((s) => s.name === name);

  const total = form.samples.length;
  const rebuilt = form.samples.filter((s) => info(s.name)?.has3D).length;
  const solved = form.samples.filter((s) => info(s.name)?.hasBallistic).length;
  const maxVideos = form.samples.reduce((m, s) => Math.max(m, info(s.name)?.videos ?? 0), 0);

  return (
    <div className="scroll">
      <div className="page">
        <div className="head">
          <div>
            <div className="title">分析</div>
            <div className="sub">
              {selected.length === 0
                ? "未选择时按全量队列执行"
                : `已选样本：${selected.length}`}
            </div>
          </div>
          <div className="head-actions">
            <button className="btn" onClick={() => runCommand("refresh")} disabled={run.running}>
              刷新样本列表
            </button>
          </div>
        </div>

        <div className="cols tall">
          <section className="card">
            <div className="sect grow">
              <div className="sec-head">
                <span className="sec-title">待处理样本</span>
                <span className="sec-right">
                  共 <b className="tabular">{total}</b> 个
                </span>
              </div>

              {total === 0 ? (
                <div className="empty-box">工作区内无样本，请通过「采集」录入或放入 samples/ 目录</div>
              ) : (
                <div className="tbl-wrap">
                  <table className={total >= 2 ? "tbl fill" : "tbl"}>
                    <thead>
                      <tr>
                        <th style={{ width: "6%" }} />
                        <th style={{ width: "26%" }}>样本</th>
                        <th style={{ width: "12%" }}>通道</th>
                        <th style={{ width: "16%" }}>三维重建</th>
                        <th style={{ width: "18%" }}>弹道解算</th>
                        <th style={{ width: "22%" }}>落点判定</th>
                      </tr>
                    </thead>
                    <tbody>
                      {form.samples.map((s) => {
                        const d = info(s.name);
                        return (
                          <tr
                            key={s.name}
                            style={{ cursor: "pointer" }}
                            onClick={() => toggleSample(s.name)}
                          >
                            <td>
                              <input
                                type="checkbox"
                                checked={selectedSet.has(s.name)}
                                readOnly
                                style={{ accentColor: "var(--brand-600)" }}
                              />
                            </td>
                            <td>
                              <span className="c-name">
                                <span className="n">{s.name}</span>
                              </span>
                            </td>
                            <td className="tabular">{d ? `${d.videos} 路` : "—"}</td>
                            <td className={d?.has3D ? "calib-ok" : "calib-no"}>
                              {d?.has3D ? "已生成" : "待执行"}
                            </td>
                            <td className={d?.hasBallistic ? "calib-ok" : "calib-no"}>
                              {d?.hasBallistic ? "已生成" : "待执行"}
                            </td>
                            <td>
                              <span
                                className={
                                  d?.verdict === "射正"
                                    ? "judge ok"
                                    : d?.verdict === "射偏"
                                      ? "judge miss"
                                      : "judge none"
                                }
                              >
                                {d?.shotSummary || "未解算"}
                              </span>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}

              <div className="queue-sum">
                <div className="q">
                  <span className="q-k">队列样本</span>
                  <span className="q-v tabular">{total}</span>
                </div>
                <div className="q">
                  <span className="q-k">视频通道</span>
                  <span className="q-v tabular">{maxVideos > 0 ? `${maxVideos} 路` : "—"}</span>
                </div>
                <div className="q">
                  <span className="q-k">已重建</span>
                  <span className="q-v tabular">{rebuilt}</span>
                </div>
                <div className="q">
                  <span className="q-k">已解算</span>
                  <span className="q-v tabular">{solved}</span>
                </div>
              </div>
            </div>
          </section>

          <section className="card">
            <div className="sect grow spread">
              <div className="sec-head">
                <span className="sec-title">分析参数</span>
              </div>

              <div className="field">
                <span className="k">门将模型</span>
                <select
                  className="select"
                  value={form.selectedGoalkeeper}
                  onChange={(e) => setField("goalkeeper", e.target.value)}
                >
                  <option value="">不启用</option>
                  {goalkeepers.map((g) => (
                    <option key={g.name} value={g.name}>
                      {g.displayName || g.name}
                    </option>
                  ))}
                </select>
              </div>

              <div className="check-row" style={{ marginTop: 4 }}>
                <label className="check">
                  <input
                    type="checkbox"
                    checked={form.skipReconstruct}
                    onChange={(e) => setField("skipReconstruct", e.target.checked)}
                  />
                  跳过三维重建阶段
                </label>
                <label className="check">
                  <input
                    type="checkbox"
                    checked={form.skipBallistic}
                    onChange={(e) => setField("skipBallistic", e.target.checked)}
                  />
                  跳过弹道解算阶段
                </label>
              </div>

              <div className="actions">
                <button
                  className="btn primary"
                  onClick={() => runCommand("pipeline.runOffline")}
                  disabled={run.running || !env.ready || total === 0}
                >
                  启动分析
                </button>
                <EnvironmentSetup env={env} />
                {run.running && (
                  <button className="btn danger" onClick={() => runCommand("pipeline.cancel")}>
                    中止
                  </button>
                )}
              </div>
              {!env.ready && (
                <div className="notice warn" role="status">{env.statusText || "请先安装 Python 推理环境"}</div>
              )}
            </div>

            <div className="sect grow">
              <div className="sec-head">
                <span className="sec-title">执行进度</span>
                <span className="sec-right">
                  <b className="tabular">{run.progress}</b>%
                </span>
              </div>

              <div className={run.running ? "progress" : "progress idle"}>
                <i style={{ width: `${Math.max(run.running ? 3 : 0, run.progress)}%` }} />
              </div>
              <div className="progress-text" title={run.progressText}>
                {run.progressText || "待启动"}
              </div>

              {run.steps.length > 0 && (
                <div className="run-steps grow">
                  {run.steps.map((step, i) => (
                    <div className={`run-step ${step.status}`} key={`${step.name}-${i}`}>
                      <span className="rs-dot">{stepIcon[step.status] ?? "○"}</span>
                      <span className="rs-name">
                        {step.name}
                        {step.description && <span className="rs-desc">{step.description}</span>}
                      </span>
                      <span className="rs-status">{stepLabel[step.status] ?? step.status}</span>
                    </div>
                  ))}
                </div>
              )}
            </div>

            {/* 点球手画像：由训练 CSV 归纳，属于分析产出，不再挂在回放页 */}
            <div className="sect">
              <div className="sec-head">
                <span className="sec-title">点球手画像</span>
                <span className="sec-note">
                  {state.shooter.hasData ? state.shooter.countText : "未生成"}
                </span>
                <span className="sec-right">
                  <button
                    className="btn sm"
                    onClick={() => runCommand("analyze.shooterProfile")}
                  >
                    生成
                  </button>
                </span>
              </div>

              {state.shooter.hasData ? (
                <div className="shooter-box">
                  <div className="shooter-metrics">
                    {state.shooter.lines.map((line) => {
                      const parts = line.split(/\s{2,}/);
                      const key = parts[0];
                      const value = parts.slice(1).join(" ");
                      return (
                        <div className="metric" key={line}>
                          <span className="metric-k">{key}</span>
                          <span className="metric-v tabular">{value}</span>
                        </div>
                      );
                    })}
                  </div>
                  <div className="shooter-radar">
                    <Radar
                      values={state.shooter.radarValues}
                      labels={state.shooter.radarLabels}
                      height={104}
                    />
                  </div>
                </div>
              ) : (
                <div className="empty-box" style={{ padding: "16px 0" }}>
                  {state.shooter.emptyText || "尚未生成"}
                </div>
              )}

            </div>
          </section>
        </div>
      </div>
    </div>
  );
}
