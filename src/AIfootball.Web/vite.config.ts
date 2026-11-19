import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// WebView2 通过虚拟主机映射（https://app.local/）加载 dist 目录，
// 因此资源必须用相对路径，不能假设站点根目录。
export default defineConfig({
  base: "./",
  plugins: [react()],
  server: { port: 5173, strictPort: true },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    target: "chrome110",
    assetsDir: "assets",
    sourcemap: false,
  },
});
