/**
 * 通用雷达图。门将属性是 6 维（60°一条轴），点球手画像是 5 维，
 * 所以按数据长度自动算角度，不写死 6。
 */
export function Radar({
  values,
  labels,
  height = 148,
}: {
  values: number[];
  labels: string[];
  height?: number;
}) {
  const n = values.length;
  if (n < 3) return null;

  const cx = 80;
  const cy = height / 2;
  const r = Math.min(46, height / 3.4);
  const step = 360 / n;

  const point = (i: number, ratio: number) => {
    const a = (-90 + i * step) * (Math.PI / 180);
    return [cx + Math.cos(a) * r * ratio, cy + Math.sin(a) * r * ratio] as const;
  };

  const poly = values
    .map((v, i) => point(i, Math.max(0.08, Math.min(1, v))).join(","))
    .join(" ");

  return (
    <svg viewBox={`0 0 160 ${height}`} width="100%" height={height} role="img" aria-label="特征雷达图">
      {[0.33, 0.66, 1].map((ratio) => (
        <polygon
          key={ratio}
          points={values.map((_, i) => point(i, ratio).join(",")).join(" ")}
          fill="none"
          stroke="var(--line)"
          strokeWidth="0.8"
        />
      ))}
      {values.map((_, i) => {
        const [x, y] = point(i, 1);
        return (
          <line key={i} x1={cx} y1={cy} x2={x} y2={y} stroke="var(--line-soft)" strokeWidth="0.8" />
        );
      })}
      <polygon points={poly} fill="rgba(30,64,175,0.16)" stroke="var(--brand-600)" strokeWidth="1.2" />
      {labels.map((label, i) => {
        const [x, y] = point(i, 1.24);
        return (
          <text
            key={label}
            x={x}
            y={y}
            fontSize="10.5"
            fill="var(--ink-3)"
            textAnchor="middle"
            dominantBaseline="central"
          >
            {label}
          </text>
        );
      })}
    </svg>
  );
}
