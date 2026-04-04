import { resolve } from "node:path";
import { defineConfig } from "vite";

export default defineConfig({
  build: {
    lib: {
      entry: resolve(import.meta.dirname, "src/main.ts"),
      name: "SkipMeDashboard",
      formats: ["iife"],
      fileName: () => "index.js",
      cssFileName: "index",
    },
    outDir: "../SkipMe.Db.Plugin/Configuration",
    emptyOutDir: false,
  },
});
