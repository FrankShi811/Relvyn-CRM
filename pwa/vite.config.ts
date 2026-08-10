import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  base: "/Relvyn-CRM/",
  plugins: [react()],
  build: { sourcemap: true }
});
