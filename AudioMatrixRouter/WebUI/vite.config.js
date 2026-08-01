import fs from "node:fs";
import path from "node:path";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Desktop-only build target. The output is bundled into the WebView2 host
// via `<Content Include="WebUI\dist\**\*">` in AudioMatrixRouter.csproj and
// served from the `appassets.local` virtual host. Relative asset URLs so
// paths resolve regardless of host scheme.
const versionFilePath = path.resolve(__dirname, "../../VERSION");
const appVersion = (process.env.VITE_APP_VERSION || fs.readFileSync(versionFilePath, "utf8")).trim();

export default defineConfig({
  base: "./",
  define: {
    __APP_VERSION__: JSON.stringify(appVersion),
  },
  plugins: [react()],
});
