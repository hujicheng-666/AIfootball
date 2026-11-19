import { useState } from "react";
import type { AppState, CalibrationState, Keeper } from "../lib/bridge";
import { runCommand } from "../lib/bridge";

function FileField({
  label,
  value,
  slot,
}: {
  label: string;
  value: string;
  slot: "leftChess" | "rightChess" | "leftField" | "rightField";
}) {
  return (
    <>
      <div className="file-label">{label}</div>
      <div className="file-field">
        <div className={value ? "file-box has" : "file-box"} title={value || undefined}>
          <span>{value || "未选择"}</span>
        </div>
        <button className="btn" onClick={() => runCommand("calibration.browse", { slot })}>
          浏览
        </button>
      </div>
    </>
  );
}

function statusClass(text: string) {
  if (text.startsWith("内参标定完成") || text.startsWith("外参标定完成")) return "notice ok";
  if (text.startsWith("正在")) return "notice";
  return "notice warn";
}

function CalibrationPanel({ calib }: { calib: CalibrationState }) {
  if (calib.ready && !calib.showWorkflow) {
    return (
      <>
        <div className="banner">
          <div>
            <div className="t">双目立体标定已就绪</div>
            <div className="d">内参、外参文件齐备，三维重建可直接引用当前结果。</div>
          </div>
          <button className="btn" onClick={() => runCommand("calibration.recalibrate")}>
            重新标定
          </button>
        </div>

        <section className="card">
          <div className="sect">
            <div className="sec-head">
              <span className="sec-title">标定产物</span>
              <span className="sec-note">calib/ · 四份文件齐备</span>
            </div>
            <div className="artifacts four">
              <div className="artifact">
                <span className="k">左相机内参</span>
                <span className="v">intrinsics_left.npz</span>
                <span className="s">焦距 · 主点 · 畸变系数</span>
              </div>
              <div className="artifact">
                <span className="k">右相机内参</span>
                <span className="v">intrinsics_right.npz</span>
                <span className="s">焦距 · 主点 · 畸变系数</span>
              </div>
              <div className="artifact">
                <span className="k">左相机外参</span>
                <span className="v">left_pose.npz</span>
                <span className="s">相对世界坐标系位姿</span>
              </div>
              <div className="artifact">
                <span className="k">右相机外参</span>
                <span className="v">right_pose.npz</span>
                <span className="s">相对世界坐标系位姿</span>
              </div>
            </div>
          </div>
        </section>

        <div className="cols tall">
          <section className="card">
            <div className="sect grow">
              <div className="sec-head">
                <span className="sec-title">重标定触发条件</span>
                <span className="sec-note">命中任一项须重做内外参</span>
              </div>
              <div className="mid-block">
              <div className="calib-row">
                <span className="calib-k">相机位姿</span>
                <span className="calib-v">位置、高度或俯仰角变化</span>
              </div>
              <div className="calib-row">
                <span className="calib-k">光学参数</span>
                <span className="calib-v">变焦、对焦调整或镜头更换</span>
              </div>
              <div className="calib-row">
                <span className="calib-k">成像分辨率</span>
                <span className="calib-v">录制分辨率与标定参数不一致</span>
              </div>
              </div>
            </div>
          </section>

          <section className="card">
            <div className="sect grow">
              <div className="sec-head">
                <span className="sec-title">下游影响</span>
                <span className="sec-note">标定参数被谁读取</span>
              </div>
              <div className="mid-block">
              <div className="calib-row">
                <span className="calib-k">三维重建</span>
                <span className="calib-v">三角化与极线校正</span>
                <span className="calib-ok">直接引用</span>
              </div>
              <div className="calib-row">
                <span className="calib-k">弹道解算</span>
                <span className="calib-v">像素坐标转米制坐标系</span>
                <span className="calib-ok">直接引用</span>
              </div>
              <div className="calib-row">
                <span className="calib-k">覆盖写入</span>
                <span className="calib-v">重新标定按同名文件覆盖</span>
              </div>
              </div>
            </div>
          </section>
        </div>
      </>
    );
  }

  return (
    <>
      {calib.running && (
        <div className="card" style={{ marginBottom: 10 }}>
          <div className="sect">
            <div className="sec-head">
              <span className="sec-title">标定任务执行中</span>
              <span className="sec-right">
                <b className="tabular">{calib.progress}</b>%
              </span>
            </div>
            <div className="progress">
              <i style={{ width: `${Math.max(3, calib.progress)}%` }} />
            </div>
            <div className="progress-text">{calib.progressText}</div>
          </div>
        </div>
      )}

      <div className="cols">
        <div>
          <section className="card">
            <div className="sect">
              <div className="sec-head">
                <span className="sec-title">
                  <span className="num-strong">01</span> 内参标定
                </span>
                <span className="sec-note">棋盘格序列 → 焦距 · 主点 · 畸变系数</span>
              </div>
              <FileField label="左相机棋盘格序列" value={calib.leftChess} slot="leftChess" />
              <FileField label="右相机棋盘格序列" value={calib.rightChess} slot="rightChess" />
              <div className="hint">
                标定板 9×6 内角点、格距 23 mm；覆盖多角度、多距离与倾斜姿态，避开强反光，时长 15–30 s。
              </div>
              {calib.intrinsicsStatus && (
                <div className={statusClass(calib.intrinsicsStatus)}>{calib.intrinsicsStatus}</div>
              )}
              <div className="actions">
                <button
                  className="btn primary"
                  onClick={() => runCommand("calibration.runIntrinsics")}
                  disabled={calib.running}
                >
                  执行内参标定
                </button>
              </div>
            </div>
          </section>

          <section className="card">
            <div className="sect">
              <div className="sec-head">
                <span className="sec-title">
                  <span className="num-strong">02</span> 外参标定
                </span>
                <span className="sec-note">左右各一帧场景图 → 相机相对位姿</span>
              </div>
              <FileField label="左相机场景参考帧" value={calib.leftField} slot="leftField" />
              <FileField label="右相机场景参考帧" value={calib.rightField} slot="rightField" />
              <div className="hint">前置条件：内参已完成。执行后按提示在图像窗口依次标注参考点。</div>
              {calib.extrinsicsStatus && (
                <div className={statusClass(calib.extrinsicsStatus)}>{calib.extrinsicsStatus}</div>
              )}
              <div className="actions">
                <button
                  className="btn ok"
                  onClick={() => runCommand("calibration.runExtrinsics")}
                  disabled={calib.running}
                >
                  执行外参标定
                </button>
              </div>
            </div>
          </section>
        </div>

        <section className="card">
          <div className="sect grow spread">
            <div className="sec-head">
              <span className="sec-title">标定状态</span>
            </div>
            <div className="calib-row">
              <span className="calib-k">内参</span>
              <span className="calib-v">{calib.intrinsics ? "已生成" : "未生成"}</span>
              <span className={calib.intrinsics ? "calib-ok" : "calib-no"}>
                {calib.intrinsics ? "就绪" : "待标定"}
              </span>
            </div>
            <div className="calib-row">
              <span className="calib-k">外参</span>
              <span className="calib-v">{calib.extrinsics ? "已生成" : "未生成"}</span>
              <span className={calib.extrinsics ? "calib-ok" : "calib-no"}>
                {calib.extrinsics ? "就绪" : "待标定"}
              </span>
            </div>
            <div className="hint" style={{ marginTop: 10 }}>
              标定结果写入 calib/，由三维重建阶段直接读取。
            </div>
          </div>
        </section>
      </div>
    </>
  );
}

const dimsOf = (k: Keeper) => [
  { label: "扑救", v: k.attrs.goalKeeping, min: 0.6, max: 1 },
  { label: "下地", v: k.attrs.diveSpeed, min: 3, max: 5 },
  { label: "横移", v: k.attrs.jumpDistance, min: 0.8, max: 1.4 },
  { label: "弹跳", v: k.attrs.jumpHeight, min: 0.3, max: 0.8 },
  { label: "臂展", v: k.attrs.reach, min: 0.3, max: 0.8 },
  { label: "封堵", v: k.attrs.tendGoalSpeed, min: 2, max: 4 },
];

function Radar({ keeper }: { keeper: Keeper }) {
  const cx = 80;
  const cy = 76;
  const r = 46;
  const dims = dimsOf(keeper);
  const point = (i: number, ratio: number) => {
    const a = (-90 + i * 60) * (Math.PI / 180);
    return [cx + Math.cos(a) * r * ratio, cy + Math.sin(a) * r * ratio] as const;
  };
  const poly = dims
    .map((d, i) => {
      const ratio = Math.max(0.08, Math.min(1, (d.v - d.min) / (d.max - d.min)));
      return point(i, ratio).join(",");
    })
    .join(" ");

  return (
    <svg viewBox="0 0 160 152" width="100%" height="152" role="img" aria-label="门将属性雷达图">
      {[0.33, 0.66, 1].map((ratio) => (
        <polygon
          key={ratio}
          points={dims.map((_, i) => point(i, ratio).join(",")).join(" ")}
          fill="none"
          stroke="var(--line)"
          strokeWidth="0.8"
        />
      ))}
      {dims.map((_, i) => {
        const [x, y] = point(i, 1);
        return <line key={i} x1={cx} y1={cy} x2={x} y2={y} stroke="var(--line-soft)" strokeWidth="0.8" />;
      })}
      <polygon points={poly} fill="rgba(30,64,175,0.16)" stroke="var(--brand-600)" strokeWidth="1.2" />
      {dims.map((d, i) => {
        const [x, y] = point(i, 1.26);
        return (
          <text
            key={d.label}
            x={x}
            y={y}
            fontSize="10.5"
            fill="var(--ink-3)"
            textAnchor="middle"
            dominantBaseline="central"
          >
            {d.label}
          </text>
        );
      })}
    </svg>
  );
}

function ZoneMap({ keeper }: { keeper: Keeper }) {
  if (!keeper.hasZones || keeper.zones.length < 15) {
    return <div className="empty-box">该门将配置未包含区域概率场</div>;
  }
  // zones 是 row-major 且第一行是低位，标签不能写反。
  const rows = ["低", "中", "高"];
  return (
    <div className="zone-wrap">
      {rows.map((label, row) => (
        <div className="zone-row" key={label}>
          <span className="zone-label">{label}</span>
          {keeper.zones.slice(row * 5, row * 5 + 5).map((p, col) => (
            <span
              key={col}
              className="zone-cell"
              style={{ background: `rgba(30, 64, 175, ${0.06 + Math.min(1, p) * 0.8})` }}
              title={p.toFixed(2)}
            />
          ))}
        </div>
      ))}
    </div>
  );
}

function KeepersPanel({ state }: { state: AppState }) {
  const { goalkeepers } = state;
  const [picked, setPicked] = useState(goalkeepers[0]?.name ?? "");
  const keeper = goalkeepers.find((g) => g.name === picked) ?? goalkeepers[0];

  if (goalkeepers.length === 0) {
    return <div className="empty-box">data/goalkeepers 下无门将配置</div>;
  }

  return (
    <div className="cols tall">
      <section className="card">
        <div className="sect">
          <div className="sec-head">
            <span className="sec-title">门将模型</span>
            <span className="sec-right">
              共 <b className="tabular">{goalkeepers.length}</b> 个
            </span>
          </div>
          <table className="tbl">
            <thead>
              <tr>
                <th style={{ width: "52%" }}>门将</th>
                <th style={{ width: "24%" }}>综合扑救</th>
                <th style={{ width: "24%" }}>区域概率场</th>
              </tr>
            </thead>
            <tbody>
              {goalkeepers.map((g) => (
                <tr
                  key={g.name}
                  style={{ cursor: "pointer", background: g.name === keeper?.name ? "var(--brand-50)" : undefined }}
                  onClick={() => setPicked(g.name)}
                >
                  <td>
                    <span className="c-name">
                      <span className="n">{g.displayName || g.name}</span>
                    </span>
                  </td>
                  <td className="tabular">{g.attrs.goalKeeping.toFixed(2)}</td>
                  <td className={g.hasZones ? "calib-ok" : "calib-no"}>
                    {g.hasZones ? "已配置" : "未配置"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="hint" style={{ marginTop: 8 }}>
            属性源文件 data/goalkeepers/*.json，与 Unity 播放器共用同一份配置。
          </div>
        </div>
      </section>

      {keeper && (
        <section className="card">
          <div className="sect">
            <div className="sec-head">
              <span className="sec-title">{keeper.displayName || keeper.name}</span>
              <span className="sec-note">{keeper.description || "—"}</span>
            </div>
            <Radar keeper={keeper} />
          </div>

          <div className="sect grow">
            <div className="sec-head" style={{ marginBottom: 6 }}>
              <span className="sec-title">区域扑救概率场</span>
              <span className="sec-note">色深与扑救概率正相关</span>
            </div>
            <ZoneMap keeper={keeper} />
          </div>

          <div className="sect grow spread">
            <div className="sec-head">
              <span className="sec-title">属性明细</span>
              <span className="sec-note">与 Unity 动作参数一一对应</span>
            </div>
            <div className="attr-list">
              {dimsOf(keeper).map((d) => (
                <div className="attr-item" key={d.label}>
                  <span className="attr-k">{d.label}</span>
                  <span className="attr-v tabular">{d.v.toFixed(2)}</span>
                </div>
              ))}
            </div>
          </div>
        </section>
      )}
    </div>
  );
}

function EnvPanel({ state }: { state: AppState }) {
  const { env, gpu, calibration } = state;
  return (
    <div className="grid3">
      <section className="card">
        <div className="sect">
          <div className="sec-head">
            <span className="sec-title">计算环境</span>
          </div>
          <div className="calib-row">
            <span className="calib-k">状态</span>
            <span className="calib-v">{env.statusText || "未检测"}</span>
            <span className={env.ready ? "calib-ok" : "calib-no"}>
              {env.ready ? "通过" : "异常"}
            </span>
          </div>
          <div className="calib-row">
            <span className="calib-k">标定</span>
            <span className="calib-v">
              {calibration.intrinsics ? "内参已生成" : "内参未生成"} ·{" "}
              {calibration.extrinsics ? "外参已生成" : "外参未生成"}
            </span>
          </div>
          <div className="hint" style={{ marginTop: 10 }}>
            自检命令 aifootball selfcheck，结论与命令行一致。
          </div>
        </div>
      </section>

      <section className="card">
        <div className="sect">
          <div className="sec-head">
            <span className="sec-title">图形设备</span>
          </div>
          <div className="calib-row">
            <span className="calib-k">显卡</span>
            <span className="calib-v" title={gpu.name || undefined}>
              {gpu.name || "未检测到独立显卡"}
            </span>
            <span className={gpu.cudaAvailable ? "calib-ok" : "calib-no"}>
              {gpu.cudaAvailable ? "CUDA" : "CPU"}
            </span>
          </div>
          <div className="calib-row">
            <span className="calib-k">CUDA</span>
            <span className="calib-v">{gpu.cudaVersion || "不可用"}</span>
          </div>
        </div>
      </section>

      <section className="card">
        <div className="sect">
          <div className="sec-head">
            <span className="sec-title">版本与组件</span>
          </div>
          <div className="calib-row">
            <span className="calib-k">应用版本</span>
            <span className="calib-v tabular">AIfootball {state.version}</span>
          </div>
          <div className="calib-row">
            <span className="calib-k">界面</span>
            <span className="calib-v">WebView2 · React</span>
          </div>
          <div className="calib-row">
            <span className="calib-k">渲染</span>
            <span className="calib-v">Unity 内嵌运行时</span>
          </div>
        </div>
      </section>
    </div>
  );
}

export function SettingsPage({ state }: { state: AppState }) {
  const [tab, setTab] = useState<"calibration" | "env" | "keepers">(() => {
    // 浏览器预览：#settings/keepers 直接看门将库；WebView2 里 hash 为空，从标定开始。
    const h = window.location.hash;
    if (h.includes("keepers")) return "keepers";
    if (h.includes("env")) return "env";
    return "calibration";
  });

  return (
    <div className="scroll">
      <div className="page">
        <div className="head">
          <div>
            <div className="title">设置</div>
            <div className="sub">标定参数、门将模型与环境诊断</div>
          </div>
        </div>

        <div className="tabs">
          <button
            className={tab === "calibration" ? "tab on" : "tab"}
            onClick={() => setTab("calibration")}
          >
            相机标定
          </button>
          <button
            className={tab === "keepers" ? "tab on" : "tab"}
            onClick={() => setTab("keepers")}
          >
            门将模型
          </button>
          <button className={tab === "env" ? "tab on" : "tab"} onClick={() => setTab("env")}>
            环境诊断
          </button>
        </div>

        {tab === "calibration" && <CalibrationPanel calib={state.calibration} />}
        {tab === "keepers" && <KeepersPanel state={state} />}
        {tab === "env" && <EnvPanel state={state} />}
      </div>
    </div>
  );
}
