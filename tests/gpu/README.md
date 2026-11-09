# GPU selection verification

Run the device parsing, ordering, fallback policy, and legacy-settings checks without a model or GPU:

```powershell
dotnet run --project tests/gpu/GpuVerify.csproj -c Release
```

The full integration/benchmark mode requires an NVIDIA CUDA device, a Vulkan integrated GPU, the prepared engine directory, and downloaded 0.8B and 2B models:

```powershell
./scripts/Prepare-Engine.ps1
dotnet run --project tests/gpu/GpuVerify.csproj -c Release -- D:/Project/MyPLaMo/engine D:/Project/MyPLaMo/models D:/Project/MyPLaMo/build/gpu-verification.json
```

Each detected backend/device and CPU performs real English-to-Japanese translation. The benchmark records model load time separately from the median of three translation requests on a loaded model; repeated prompts may reuse the engine's prompt cache. The test also checks server reuse, disposal, automatic CUDA selection, unavailable-device CPU fallback, Vulkan fallback without CUDA, and cancellation. The missing-CUDA test temporarily renames `engine/cuda` and restores it in `finally`; run it while that engine directory is not used by another application.

`../gpu-ui.ps1` verifies the published application's settings and translations on an RTX 5090 + AMD integrated GPU host. Launch `release/MyPLaMo/MyPLaMo.exe` with a dedicated `MYPLAMO_PROFILE` containing settings that select an existing 0.8B model, disable Chrome integration and the global shortcut, and initially use automatic GPU selection. Supply that process ID and profile directory:

```powershell
./tests/gpu-ui.ps1 -AppPid 1234 -ProfileDirectory D:/Project/MyPLaMo/build/gpu-ui-profile
```

The UI test uses local UI Automation, captures light/dark states, verifies all four execution choices, cancellation and persistence, and checks that saving CPU mode terminates the previously loaded GPU server. It records whole-device NVIDIA memory usage for reference; do not run other GPU workloads concurrently if interpreting those numbers. Inspect the generated screenshots as well as `results.json`.
