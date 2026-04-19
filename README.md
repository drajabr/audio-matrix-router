# audio router matrix

Windows audio router matrix for device and channel crosspoint routing with gain, phase, and live meters.

## links

- repository: https://github.com/drajabr/audio-router-matrix
- pages preview: https://drajabr.github.io/audio-router-matrix/
- releases: https://github.com/drajabr/audio-router-matrix/releases

## quick start

```powershell
git clone https://github.com/drajabr/audio-router-matrix.git
cd audio-router-matrix
./build-all.ps1
./build/desktop/AudioMatrixRouter.exe
```

## development

```powershell
cd AudioMatrixRouter/WebUI
npm ci
npm run dev
```

```powershell
dotnet run --project AudioMatrixRouter
```

## ci/cd

- build: .github/workflows/ci.yml
- pages: .github/workflows/pages.yml
- release: .github/workflows/release.yml

## license

MIT. See LICENSE.
