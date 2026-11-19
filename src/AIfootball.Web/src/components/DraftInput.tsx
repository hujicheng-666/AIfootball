import { useEffect, useRef, useState } from "react";

/**
 * 文本输入的特殊性：值由宿主持有，但每敲一个字就往返一次会让光标打架。
 * 这里在本地先存草稿，失焦或回车才提交，宿主回推后再对齐。
 */
export function DraftInput({
  value,
  onCommit,
  placeholder,
  className = "input",
  width,
}: {
  value: string;
  onCommit: (next: string) => void;
  placeholder?: string;
  className?: string;
  width?: number | string;
}) {
  const [draft, setDraft] = useState(value);
  const committed = useRef(value);

  useEffect(() => {
    // 只有宿主侧真的变了（例如切页回来）才覆盖草稿，避免打断正在输入的内容。
    if (value !== committed.current) {
      committed.current = value;
      setDraft(value);
    }
  }, [value]);

  const commit = () => {
    if (draft === committed.current) return;
    committed.current = draft;
    onCommit(draft);
  };

  return (
    <input
      className={className}
      style={width === undefined ? undefined : { width }}
      value={draft}
      placeholder={placeholder}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === "Enter") e.currentTarget.blur();
        if (e.key === "Escape") {
          setDraft(committed.current);
          e.currentTarget.blur();
        }
      }}
    />
  );
}
