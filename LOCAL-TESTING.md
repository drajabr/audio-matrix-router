# Local Testing vs GitHub Actions Deployment

## Build Modes

The app uses two different base paths depending on the build mode:

- **`web` mode** (`npm run build:web`): Uses `/audio-matrix-router/` base path for GitHub Pages deployment
- **`windows` mode** (`npm run build:windows`): Uses `./` base path for desktop app embedding and local preview

## Local Testing (Development & QA)

The build-all.ps1 creates both web and windows builds, then automatically starts a preview server:

```powershell
# 1. Build everything and start preview
.\build-all.ps1
```

This will:
1. Build web production assets (for GitHub Pages deployment)
2. Build desktop app
3. Build windows mode assets (root base path)
4. Start local preview server on `http://localhost:4173/` or next available port

**Why windows mode for preview?**
- Local preview serves from root `/`, not from `/audio-matrix-router/`
- Windows mode uses `./` base path, so assets load correctly
- Prevents MIME type and path errors

## Standalone Preview

If you already have a build and just want to test:

```powershell
.\preview.ps1
```

This rebuilds windows mode and starts the preview server.

## GitHub Actions Deployment

The `.github/workflows/ci.yml` handles remote deployment to Pages:

- VERSION file change triggers release automation
- Web build uses `npm run build:web` (GitHub Pages mode with `/audio-matrix-router/` base)
- Assets deployed to `https://yourdomain/audio-matrix-router/`
- No local server needed for Pages

## Development Mode (Hot Reload)

For active development with file watchers:

```powershell
cd AudioMatrixRouter\WebUI
npm run dev
```

This starts dev server at `http://localhost:5173` with live reload and root base path.

## Build Mode Comparison

| Mode | Base Path | Use Case | Command |
|------|-----------|----------|---------|
| **web** | `/audio-matrix-router/` | GitHub Pages deployment | `npm run build:web` |
| **windows** | `./` | Local preview + desktop embedding | `npm run build:windows` |
| **dev** | `/` | Development with hot reload | `npm run dev` |

## Summary

- **Local testing**: `.\build-all.ps1` (auto-starts preview) or `.\preview.ps1`
- **Standalone preview**: `.\preview.ps1` (builds windows mode + preview)
- **Pages deployment**: GitHub Actions auto-deploys on VERSION change
- **Development**: `npm run dev` with active code changes
